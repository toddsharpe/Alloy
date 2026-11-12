using System;
using System.Collections.Generic;
using System.Text;

namespace Alloy.Sdk.Script
{
	internal enum CompletionType
	{
		Normal,
		Return,
		Break,
		Continue,
	}

	// How a statement finished: normally, or with a return, break or continue travelling outwards
	internal readonly struct Completion
	{
		public readonly CompletionType Type;
		public readonly JsValue Value;
		public readonly string Label;
		public readonly bool HasValue;

		public Completion(CompletionType type, JsValue value, string label, bool hasValue = true)
		{
			Type = type;
			Value = value;
			Label = label;
			HasValue = hasValue;
		}

		public static readonly Completion Normal = new Completion(CompletionType.Normal, JsValue.Undefined, null, false);

		public static Completion Of(JsValue value) => new Completion(CompletionType.Normal, value, null);

		public bool IsAbrupt => Type != CompletionType.Normal;
	}

	// A tree-walking interpreter: statements run for their completions, expressions for their values
	public sealed partial class Interpreter
	{
		private struct Frame
		{
			public string Name;
			public int CallLine;
		}

		public static Interpreter Current;
		public readonly JsObject Global;
		public readonly Scope GlobalScope;
		public int MaxDepth = 2000;
		public int CurrentLine;
		public Action<string> OnError;
		[ThreadStatic]
		private static int _depth;
		[ThreadStatic]
		private static List<Frame> _frames;

		public Interpreter()
		{
			Global = new JsObject(null);
			GlobalScope = new Scope(null, true) { HasThis = true };
			GlobalScope.ThisValue = Global;
			Interpreter prior = Current;
			Current = this;
			try
			{
				SetupRealm();
			}
			finally
			{
				Current = prior;
			}
		}

		// Parses and runs a script in the global scope, then the jobs it queued; syntax errors arrive as thrown SyntaxError objects
		public JsValue Evaluate(string source)
		{
			Interpreter prior = Current;
			Current = this;
			try
			{
				Program program;
				try
				{
					program = Parser.Parse(source);
				}
				catch (SyntaxError e)
				{
					throw new JsThrow(NewError("SyntaxError", e.Message));
				}
				return Run(program);
			}
			finally
			{
				Current = prior;
			}
		}

		// Runs a script and reports an uncaught error instead of throwing it, as a page's script tag would
		public bool TryEvaluate(string source)
		{
			try
			{
				Evaluate(source);
				return true;
			}
			catch (JsThrow t)
			{
				ReportError(t.Value);
				return false;
			}
		}

		public JsValue Run(Program program)
		{
			Interpreter prior = Current;
			Current = this;
			try
			{
				HoistVars(program.Body, GlobalScope, true);
				HoistLexical(program.Body, GlobalScope, true);
				JsValue last = JsValue.Undefined;
				foreach (Statement statement in program.Body)
				{
					Completion completion = Execute(statement, GlobalScope);
					if (completion.HasValue)
						last = completion.Value;
				}
				RunJobs();
				return last;
			}
			finally
			{
				Current = prior;
			}
		}

		public void ReportError(JsValue error) => OnError?.Invoke("Uncaught " + DescribeError(error));

		public string DescribeError(JsValue error)
		{
			if (error.IsObject && error.Object.Class == "Error")
			{
				JsValue stack = error.Object.Get("stack", this);
				if (stack.IsString)
					return stack.String;
			}
			return error.IsObject ? ToDisplayString(error) : error.ToJsString();
		}

		// Statements

		private Completion Execute(Statement statement, Scope scope)
		{
			CurrentLine = statement.Line;
			switch (statement)
			{
				case ExpressionStatement e:
					return Completion.Of(Evaluate(e.Expression, scope));
				case VariableDeclaration d:
					ExecuteDeclaration(d, scope);
					return Completion.Normal;
				case FunctionDeclaration:
				case EmptyStatement:
					return Completion.Normal;
				case ClassDeclaration c:
				{
					JsFunction cls = EvaluateClass(c.Class, scope);
					InitializeLexical(scope, c.Class.Name, cls, true);
					return Completion.Normal;
				}
				case ReturnStatement r:
					return new Completion(CompletionType.Return, r.Argument == null ? JsValue.Undefined : Evaluate(r.Argument, scope), null);
				case IfStatement i:
					if (Evaluate(i.Test, scope).Truthy)
						return Execute(i.Consequent, scope);
					return i.Alternate != null ? Execute(i.Alternate, scope) : Completion.Normal;
				case BlockStatement b:
					return ExecuteBlock(b.Body, scope);
				case ForStatement f:
					return ExecuteFor(f, scope);
				case ForInStatement fi:
					return fi.IsOf ? ExecuteForOf(fi, scope) : ExecuteForIn(fi, scope);
				case WhileStatement w:
					return ExecuteWhile(w, scope);
				case BreakStatement b:
					return new Completion(CompletionType.Break, JsValue.Undefined, b.Label);
				case ContinueStatement c:
					return new Completion(CompletionType.Continue, JsValue.Undefined, c.Label);
				case ThrowStatement t:
					throw new JsThrow(Evaluate(t.Argument, scope));
				case TryStatement t:
					return ExecuteTry(t, scope);
				case SwitchStatement s:
					return ExecuteSwitch(s, scope);
				case LabeledStatement l:
				{
					Completion completion = Execute(l.Body, scope);
					if (completion.Type == CompletionType.Break && completion.Label == l.Label)
						return Completion.Normal;
					return completion;
				}
				default:
					throw new InvalidOperationException("unknown statement " + statement.GetType().Name);
			}
		}

		private Completion ExecuteBlock(List<Statement> body, Scope parent)
		{
			Scope scope = parent;
			if (NeedsScope(body))
			{
				scope = new Scope(parent, false);
				HoistLexical(body, scope, false);
			}
			Completion last = Completion.Normal;
			foreach (Statement statement in body)
			{
				Completion completion = Execute(statement, scope);
				if (completion.IsAbrupt)
					return completion;
				if (completion.HasValue)
					last = completion;
			}
			return last;
		}

		private static bool NeedsScope(List<Statement> body)
		{
			foreach (Statement statement in body)
			{
				if (statement is VariableDeclaration { Kind: not DeclarationKind.Var } || statement is ClassDeclaration || statement is FunctionDeclaration)
					return true;
			}
			return false;
		}

		// A loop body's completion: true to leave the loop, with what the loop itself completes with
		private static bool LoopExit(Completion completion, string label, out Completion result)
		{
			result = Completion.Normal;
			switch (completion.Type)
			{
				case CompletionType.Break:
					if (completion.Label != null && completion.Label != label)
						result = completion;
					return true;
				case CompletionType.Continue:
					if (completion.Label == null || completion.Label == label)
						return false;
					result = completion;
					return true;
				case CompletionType.Return:
					result = completion;
					return true;
				default:
					return false;
			}
		}

		private Completion ExecuteWhile(WhileStatement loop, Scope scope)
		{
			if (loop.IsDoWhile)
			{
				do
				{
					if (LoopExit(Execute(loop.Body, scope), loop.Label, out Completion result))
						return result;
				}
				while (Evaluate(loop.Test, scope).Truthy);
				return Completion.Normal;
			}
			while (Evaluate(loop.Test, scope).Truthy)
			{
				if (LoopExit(Execute(loop.Body, scope), loop.Label, out Completion result))
					return result;
			}
			return Completion.Normal;
		}

		private Completion ExecuteFor(ForStatement loop, Scope scope)
		{
			// let and const loop variables get a fresh scope each iteration, so closures see their own copy
			bool perIteration = loop.Init is VariableDeclaration { Kind: not DeclarationKind.Var };
			Scope iteration = scope;
			if (perIteration)
			{
				iteration = new Scope(scope, false);
				ExecuteDeclaration((VariableDeclaration)loop.Init, iteration);
				iteration = CopyScope(iteration);
			}
			else if (loop.Init is VariableDeclaration declaration)
				ExecuteDeclaration(declaration, scope);
			else if (loop.Init is Expression init)
				Evaluate(init, scope);
			while (true)
			{
				if (loop.Test != null && !Evaluate(loop.Test, iteration).Truthy)
					return Completion.Normal;
				if (LoopExit(Execute(loop.Body, iteration), loop.Label, out Completion result))
					return result;
				if (perIteration)
					iteration = CopyScope(iteration);
				if (loop.Update != null)
					Evaluate(loop.Update, iteration);
			}
		}

		private static Scope CopyScope(Scope scope)
		{
			Scope copy = new Scope(scope.Parent, false);
			foreach (KeyValuePair<string, Binding> pair in scope.Bindings)
				copy.Declare(pair.Key, pair.Value.Value, pair.Value.Mutable, pair.Value.Initialized);
			return copy;
		}

		private Completion ExecuteForOf(ForInStatement loop, Scope scope)
		{
			JsValue iterable = Evaluate(loop.Right, scope);
			bool fresh = loop.Left is VariableDeclaration { Kind: not DeclarationKind.Var };
			foreach (JsValue item in Iterate(iterable))
			{
				Scope iteration = fresh ? new Scope(scope, false) : scope;
				BindLoopTarget(loop.Left, item, iteration);
				if (LoopExit(Execute(loop.Body, iteration), loop.Label, out Completion result))
					return result;
			}
			return Completion.Normal;
		}

		private Completion ExecuteForIn(ForInStatement loop, Scope scope)
		{
			JsValue target = Evaluate(loop.Right, scope);
			if (target.IsNullish)
				return Completion.Normal;
			bool fresh = loop.Left is VariableDeclaration { Kind: not DeclarationKind.Var };
			JsObject source = ToObject(target);
			foreach (string key in EnumerableKeys(source))
			{
				if (!source.HasProperty(key))
					continue;
				Scope iteration = fresh ? new Scope(scope, false) : scope;
				BindLoopTarget(loop.Left, key, iteration);
				if (LoopExit(Execute(loop.Body, iteration), loop.Label, out Completion result))
					return result;
			}
			return Completion.Normal;
		}

		private void BindLoopTarget(Node left, JsValue value, Scope scope)
		{
			if (left is VariableDeclaration declaration)
				BindPattern(declaration.Declarations[0].Target, value, scope, declaration.Kind);
			else
				BindPattern(left, value, scope, null);
		}

		private Completion ExecuteTry(TryStatement statement, Scope scope)
		{
			Completion completion = Completion.Normal;
			Exception pending = null;
			try
			{
				completion = ExecuteBlock(statement.Block.Body, scope);
			}
			catch (Exception e) when (e is not GeneratorSignal)
			{
				if (statement.Handler == null)
					pending = e;
				else
				{
					JsValue thrown = e is JsThrow t ? t.Value : NewError("Error", "internal: " + e.GetType().Name + ": " + e.Message);
					Scope catchScope = new Scope(scope, false);
					if (statement.CatchParameter != null)
						BindPattern(statement.CatchParameter, thrown, catchScope, DeclarationKind.Let);
					try
					{
						completion = ExecuteBlock(statement.Handler.Body, catchScope);
					}
					catch (Exception inner) when (statement.Finalizer != null && inner is not GeneratorSignal)
					{
						pending = inner;
					}
				}
			}
			catch (GeneratorSignal signal) when (statement.Finalizer != null)
			{
				pending = signal;
			}
			if (statement.Finalizer != null)
			{
				Completion final = ExecuteBlock(statement.Finalizer.Body, scope);
				if (final.IsAbrupt)
					return final;
			}
			if (pending != null)
				throw pending;
			return completion;
		}

		private Completion ExecuteSwitch(SwitchStatement statement, Scope scope)
		{
			JsValue discriminant = Evaluate(statement.Discriminant, scope);
			Scope inner = new Scope(scope, false);
			List<Statement> all = new List<Statement>();
			foreach (SwitchCase c in statement.Cases)
				all.AddRange(c.Body);
			HoistLexical(all, inner, false);
			int start = -1;
			for (int i = 0; i < statement.Cases.Count && start < 0; i++)
			{
				if (statement.Cases[i].Test != null && JsValue.StrictEquals(discriminant, Evaluate(statement.Cases[i].Test, inner)))
					start = i;
			}
			if (start < 0)
				start = statement.Cases.FindIndex(c => c.Test == null);
			if (start < 0)
				return Completion.Normal;
			Completion last = Completion.Normal;
			for (int i = start; i < statement.Cases.Count; i++)
			{
				foreach (Statement body in statement.Cases[i].Body)
				{
					Completion completion = Execute(body, inner);
					if (completion.Type == CompletionType.Break && (completion.Label == null || completion.Label == statement.Label))
						return last;
					if (completion.IsAbrupt)
						return completion;
					if (completion.HasValue)
						last = completion;
				}
			}
			return last;
		}

		private void ExecuteDeclaration(VariableDeclaration declaration, Scope scope)
		{
			foreach (VariableDeclarator declarator in declaration.Declarations)
			{
				if (declarator.Init == null && declaration.Kind == DeclarationKind.Var)
					continue;
				JsValue value = declarator.Init == null ? JsValue.Undefined : EvaluateNamed(declarator.Init, scope, declarator.Target as Identifier);
				BindPattern(declarator.Target, value, scope, declaration.Kind);
			}
		}

		// Evaluates an initializer, giving an anonymous function or class the name it is being bound to
		private JsValue EvaluateNamed(Expression expression, Scope scope, Identifier target)
		{
			JsValue value = Evaluate(expression, scope);
			if (target != null && value.IsCallable && value.Function.Name == "" && (expression is FunctionExpression || expression is ClassNode))
				value.Function.Name = target.Name;
			return value;
		}

		// Hoisting: var names into the function scope, let, const, class and function declarations into their block

		private void HoistVars(List<Statement> statements, Scope scope, bool global)
		{
			foreach (Statement statement in statements)
				HoistVarsIn(statement, scope, global);
		}

		private void HoistVarsIn(Statement statement, Scope scope, bool global)
		{
			switch (statement)
			{
				case VariableDeclaration { Kind: DeclarationKind.Var } declaration:
					foreach (VariableDeclarator declarator in declaration.Declarations)
					{
						foreach (string name in PatternNames(declarator.Target))
							DeclareVar(name, scope, global);
					}
					break;
				case BlockStatement block:
					HoistVars(block.Body, scope, global);
					break;
				case IfStatement i:
					HoistVarsIn(i.Consequent, scope, global);
					if (i.Alternate != null)
						HoistVarsIn(i.Alternate, scope, global);
					break;
				case ForStatement f:
					if (f.Init is VariableDeclaration init)
						HoistVarsIn(init, scope, global);
					HoistVarsIn(f.Body, scope, global);
					break;
				case ForInStatement fi:
					if (fi.Left is VariableDeclaration left)
						HoistVarsIn(left, scope, global);
					HoistVarsIn(fi.Body, scope, global);
					break;
				case WhileStatement w:
					HoistVarsIn(w.Body, scope, global);
					break;
				case TryStatement t:
					HoistVars(t.Block.Body, scope, global);
					if (t.Handler != null)
						HoistVars(t.Handler.Body, scope, global);
					if (t.Finalizer != null)
						HoistVars(t.Finalizer.Body, scope, global);
					break;
				case SwitchStatement s:
					foreach (SwitchCase c in s.Cases)
						HoistVars(c.Body, scope, global);
					break;
				case LabeledStatement l:
					HoistVarsIn(l.Body, scope, global);
					break;
			}
		}

		private void DeclareVar(string name, Scope scope, bool global)
		{
			if (global)
			{
				if (!Global.HasOwnProperty(name))
					Global.Put(name, JsValue.Undefined);
			}
			else if (!scope.Bindings.ContainsKey(name))
				scope.Declare(name, JsValue.Undefined);
		}

		private void HoistLexical(List<Statement> statements, Scope scope, bool global)
		{
			foreach (Statement statement in statements)
			{
				switch (statement)
				{
					case FunctionDeclaration f:
					{
						JsFunction function = MakeFunction(f.Function, scope);
						if (global)
							Global.Put(f.Function.Name, function);
						else
							scope.Declare(f.Function.Name, function);
						break;
					}
					case VariableDeclaration { Kind: not DeclarationKind.Var } declaration:
						foreach (VariableDeclarator declarator in declaration.Declarations)
						{
							foreach (string name in PatternNames(declarator.Target))
								scope.Declare(name, JsValue.Undefined, declaration.Kind != DeclarationKind.Const, false);
						}
						break;
					case ClassDeclaration c:
						scope.Declare(c.Class.Name, JsValue.Undefined, true, false);
						break;
				}
			}
		}

		private static IEnumerable<string> PatternNames(Node target)
		{
			switch (target)
			{
				case Identifier id:
					yield return id.Name;
					break;
				case ArrayPattern array:
					foreach (Node element in array.Elements)
					{
						if (element == null)
							continue;
						foreach (string name in PatternNames(element))
							yield return name;
					}
					if (array.Rest != null)
					{
						foreach (string name in PatternNames(array.Rest))
							yield return name;
					}
					break;
				case ObjectPattern obj:
					foreach (PatternProperty property in obj.Properties)
					{
						foreach (string name in PatternNames(property.Target))
							yield return name;
					}
					if (obj.Rest != null)
					{
						foreach (string name in PatternNames(obj.Rest))
							yield return name;
					}
					break;
			}
		}

		// Bindings and assignment

		private JsValue LookupIdentifier(string name, Scope scope)
		{
			Binding binding = scope.Lookup(name);
			if (binding != null)
			{
				if (!binding.Initialized)
					throw ReferenceError($"Cannot access '{name}' before initialization");
				return binding.Value;
			}
			if (Global.HasProperty(name))
				return Global.Get(name, this);
			throw ReferenceError($"{name} is not defined");
		}

		private void AssignIdentifier(string name, JsValue value, Scope scope)
		{
			Binding binding = scope.Lookup(name);
			if (binding != null)
			{
				if (!binding.Initialized)
					throw ReferenceError($"Cannot access '{name}' before initialization");
				if (!binding.Mutable)
					throw TypeError("Assignment to constant variable.");
				binding.Value = value;
				return;
			}
			Global.Set(name, value, this);
		}

		// Initializes a let, const or class binding hoisted into this scope, or declares it when the loop head made no room for it
		private static void InitializeLexical(Scope scope, string name, JsValue value, bool mutable)
		{
			if (scope.Bindings.TryGetValue(name, out Binding binding))
			{
				binding.Value = value;
				binding.Initialized = true;
				binding.Mutable = mutable;
				return;
			}
			scope.Declare(name, value, mutable);
		}

		// Binds a value to a name or destructuring pattern: as a declaration of the given kind, or as plain assignment when kind is null
		private void BindPattern(Node target, JsValue value, Scope scope, DeclarationKind? kind)
		{
			switch (target)
			{
				case Identifier id:
					if (value.IsCallable && value.Function.Name == "" && value.Function.Node != null)
						value.Function.Name = id.Name;
					if (kind == null)
						AssignIdentifier(id.Name, value, scope);
					else if (kind == DeclarationKind.Var)
					{
						Binding binding = scope.Lookup(id.Name);
						if (binding != null)
						{
							binding.Value = value;
							binding.Initialized = true;
						}
						else
							Global.Set(id.Name, value, this);
					}
					else
						InitializeLexical(scope, id.Name, value, kind != DeclarationKind.Const);
					break;
				case MemberExpression member:
				{
					JsValue target2 = Evaluate(member.Object, scope);
					string key = MemberKey(member, scope);
					SetMember(target2, key, value);
					break;
				}
				case ArrayPattern array:
				{
					using IEnumerator<JsValue> items = Iterate(value).GetEnumerator();
					for (int i = 0; i < array.Elements.Count; i++)
					{
						JsValue item = items.MoveNext() ? items.Current : JsValue.Undefined;
						if (array.Elements[i] == null)
							continue;
						if (item.IsUndefined && array.Defaults[i] != null)
							item = EvaluateNamed(array.Defaults[i], scope, array.Elements[i] as Identifier);
						BindPattern(array.Elements[i], item, scope, kind);
					}
					if (array.Rest != null)
					{
						JsArray rest = NewArray();
						while (items.MoveNext())
							rest.Elements.Add(items.Current);
						BindPattern(array.Rest, rest, scope, kind);
					}
					break;
				}
				case ObjectPattern obj:
				{
					if (value.IsNullish)
						throw TypeError($"Cannot destructure '{value.ToJsString()}' as it is {value.ToJsString()}.");
					HashSet<string> taken = obj.Rest != null ? new HashSet<string>(StringComparer.Ordinal) : null;
					foreach (PatternProperty property in obj.Properties)
					{
						string key = property.ComputedKey != null ? ToPropertyKey(Evaluate(property.ComputedKey, scope)) : property.Key;
						taken?.Add(key);
						JsValue item = GetMember(value, key);
						if (item.IsUndefined && property.Default != null)
							item = EvaluateNamed(property.Default, scope, property.Target as Identifier);
						BindPattern(property.Target, item, scope, kind);
					}
					if (obj.Rest != null)
					{
						JsObject rest = NewObject();
						foreach (string key in OwnEnumerableKeys(ToObject(value)))
						{
							if (!taken.Contains(key))
								rest.Put(key, GetMember(value, key));
						}
						BindPattern(obj.Rest, rest, scope, kind);
					}
					break;
				}
				case AssignmentExpression { Operator: "=" } assignment:
				{
					if (value.IsUndefined)
						value = Evaluate(assignment.Value, scope);
					BindPattern(assignment.Target, value, scope, kind);
					break;
				}
				default:
					throw SyntaxErrorThrow("Invalid destructuring assignment target");
			}
		}

		// Expressions

		public JsValue Evaluate(Expression expression, Scope scope)
		{
			switch (expression)
			{
				case Literal literal:
					return literal.Value;
				case Identifier id:
					return LookupIdentifier(id.Name, scope);
				case MemberExpression:
				case CallExpression:
				{
					JsValue value = EvaluateChain(expression, scope, out bool broken);
					return broken ? JsValue.Undefined : value;
				}
				case BinaryExpression binary:
					return BinaryOperation(binary.Operator, Evaluate(binary.Left, scope), Evaluate(binary.Right, scope));
				case LogicalExpression logical:
				{
					JsValue left = Evaluate(logical.Left, scope);
					switch (logical.Operator)
					{
						case "&&":
							return left.Truthy ? Evaluate(logical.Right, scope) : left;
						case "||":
							return left.Truthy ? left : Evaluate(logical.Right, scope);
						default:
							return left.IsNullish ? Evaluate(logical.Right, scope) : left;
					}
				}
				case AssignmentExpression assignment:
					return EvaluateAssignment(assignment, scope);
				case UnaryExpression unary:
					return EvaluateUnary(unary, scope);
				case UpdateExpression update:
					return EvaluateUpdate(update, scope);
				case ConditionalExpression conditional:
					return Evaluate(conditional.Test, scope).Truthy ? Evaluate(conditional.Consequent, scope) : Evaluate(conditional.Alternate, scope);
				case SequenceExpression sequence:
				{
					JsValue last = JsValue.Undefined;
					foreach (Expression e in sequence.Expressions)
						last = Evaluate(e, scope);
					return last;
				}
				case ThisExpression:
					return ResolveThis(scope);
				case FunctionExpression function:
					return EvaluateFunctionExpression(function.Function, scope);
				case ClassNode cls:
					return EvaluateClass(cls, scope);
				case ArrayLiteral array:
					return EvaluateArrayLiteral(array, scope);
				case ObjectLiteral obj:
					return EvaluateObjectLiteral(obj, scope);
				case TemplateLiteral template:
					return template.Tag == null ? EvaluateTemplate(template, scope) : EvaluateTaggedTemplate(template, scope);
				case NewExpression construct:
				{
					JsValue callee = Evaluate(construct.Callee, scope);
					JsValue[] arguments = EvaluateArguments(construct.Arguments, scope);
					if (!callee.IsCallable || !callee.Function.IsConstructor)
						throw TypeError(Describe(construct.Callee) + " is not a constructor");
					return Construct(callee, arguments, callee);
				}
				case RegExpLiteral regexp:
					return NewRegExp(regexp.Pattern, regexp.Flags);
				case NewTargetExpression:
					return scope.ThisScope()?.NewTarget ?? JsValue.Undefined;
				case AwaitExpression await:
					return Await(Evaluate(await.Argument, scope));
				case YieldExpression yield:
					return EvaluateYield(yield, scope);
				case SuperExpression:
					throw SyntaxErrorThrow("'super' keyword unexpected here");
				case SpreadElement:
					throw SyntaxErrorThrow("Unexpected spread");
				default:
					throw new InvalidOperationException("unknown expression " + expression.GetType().Name);
			}
		}

		// Member accesses and calls, with optional chaining short-circuiting the rest of the chain
		private JsValue EvaluateChain(Expression expression, Scope scope, out bool broken)
		{
			broken = false;
			switch (expression)
			{
				case MemberExpression member:
				{
					if (member.Object is SuperExpression)
					{
						JsObject home = HomeOf(scope);
						JsValue thisValue = ResolveThis(scope);
						return home?.Prototype == null ? JsValue.Undefined : home.Prototype.Get(MemberKey(member, scope), this, thisValue);
					}
					JsValue target = EvaluateChain(member.Object, scope, out broken);
					if (broken)
						return JsValue.Undefined;
					if (member.Optional && target.IsNullish)
					{
						broken = true;
						return JsValue.Undefined;
					}
					string key = MemberKey(member, scope);
					if (target.IsNullish)
						throw TypeError($"Cannot read properties of {target.ToJsString()} (reading '{key}')");
					return GetMember(target, key);
				}
				case CallExpression call:
				{
					if (call.Callee is SuperExpression)
						return SuperCall(call, scope);
					JsValue thisValue = JsValue.Undefined;
					JsValue callee;
					if (call.Callee is MemberExpression member)
					{
						if (member.Object is SuperExpression)
						{
							thisValue = ResolveThis(scope);
							JsObject home = HomeOf(scope);
							callee = home?.Prototype == null ? JsValue.Undefined : home.Prototype.Get(MemberKey(member, scope), this, thisValue);
						}
						else
						{
							thisValue = EvaluateChain(member.Object, scope, out broken);
							if (broken)
								return JsValue.Undefined;
							if (member.Optional && thisValue.IsNullish)
							{
								broken = true;
								return JsValue.Undefined;
							}
							string key = MemberKey(member, scope);
							if (thisValue.IsNullish)
								throw TypeError($"Cannot read properties of {thisValue.ToJsString()} (reading '{key}')");
							callee = GetMember(thisValue, key);
						}
					}
					else
					{
						callee = EvaluateChain(call.Callee, scope, out broken);
						if (broken)
							return JsValue.Undefined;
					}
					if (call.Optional && callee.IsNullish)
					{
						broken = true;
						return JsValue.Undefined;
					}
					JsValue[] arguments = EvaluateArguments(call.Arguments, scope);
					if (!callee.IsCallable)
						throw TypeError(Describe(call.Callee) + " is not a function");
					return Call(callee.Function, thisValue, arguments);
				}
				default:
					return Evaluate(expression, scope);
			}
		}

		private string MemberKey(MemberExpression member, Scope scope) => member.Computed ? ToPropertyKey(Evaluate(member.Property, scope)) : member.Name;

		private static string Describe(Expression expression)
		{
			switch (expression)
			{
				case Identifier id:
					return id.Name;
				case MemberExpression { Computed: false } member:
					return Describe(member.Object) + "." + member.Name;
				case MemberExpression member:
					return Describe(member.Object) + "[...]";
				case ThisExpression:
					return "this";
				case CallExpression call:
					return Describe(call.Callee) + "(...)";
				default:
					return "expression";
			}
		}

		private JsValue[] EvaluateArguments(List<Expression> expressions, Scope scope)
		{
			if (expressions.Count == 0)
				return Array.Empty<JsValue>();
			bool spread = false;
			foreach (Expression e in expressions)
				spread |= e is SpreadElement;
			if (!spread)
			{
				JsValue[] values = new JsValue[expressions.Count];
				for (int i = 0; i < values.Length; i++)
					values[i] = Evaluate(expressions[i], scope);
				return values;
			}
			List<JsValue> list = new List<JsValue>();
			foreach (Expression e in expressions)
			{
				if (e is SpreadElement s)
					list.AddRange(Iterate(Evaluate(s.Argument, scope)));
				else
					list.Add(Evaluate(e, scope));
			}
			return list.ToArray();
		}

		private JsValue EvaluateAssignment(AssignmentExpression assignment, Scope scope)
		{
			switch (assignment.Operator)
			{
				case "=":
				{
					JsValue value = EvaluateNamed(assignment.Value, scope, assignment.Target as Identifier);
					if (assignment.Target is MemberExpression member)
					{
						JsValue target = Evaluate(member.Object, scope);
						string key = MemberKey(member, scope);
						SetMember(target, key, value);
					}
					else
						BindPattern(assignment.Target, value, scope, null);
					return value;
				}
				case "&&=":
				case "||=":
				case "??=":
				{
					JsValue current = Evaluate((Expression)assignment.Target, scope);
					bool assign = assignment.Operator == "&&=" ? current.Truthy : assignment.Operator == "||=" ? !current.Truthy : current.IsNullish;
					if (!assign)
						return current;
					JsValue value = EvaluateNamed(assignment.Value, scope, assignment.Target as Identifier);
					BindPattern(assignment.Target, value, scope, null);
					return value;
				}
				default:
				{
					string op = assignment.Operator.Substring(0, assignment.Operator.Length - 1);
					if (assignment.Target is MemberExpression member)
					{
						JsValue target = Evaluate(member.Object, scope);
						string key = MemberKey(member, scope);
						JsValue result = BinaryOperation(op, GetMember(target, key), Evaluate(assignment.Value, scope));
						SetMember(target, key, result);
						return result;
					}
					Identifier id = (Identifier)assignment.Target;
					JsValue value = BinaryOperation(op, LookupIdentifier(id.Name, scope), Evaluate(assignment.Value, scope));
					AssignIdentifier(id.Name, value, scope);
					return value;
				}
			}
		}

		private JsValue EvaluateUnary(UnaryExpression unary, Scope scope)
		{
			switch (unary.Operator)
			{
				case "!":
					return !Evaluate(unary.Argument, scope).Truthy;
				case "-":
					return -Evaluate(unary.Argument, scope).ToNumber();
				case "+":
					return Evaluate(unary.Argument, scope).ToNumber();
				case "~":
					return ~Evaluate(unary.Argument, scope).ToInt32();
				case "typeof":
					if (unary.Argument is Identifier id && scope.Lookup(id.Name) == null && !Global.HasProperty(id.Name))
						return "undefined";
					return Evaluate(unary.Argument, scope).TypeOf;
				case "void":
					Evaluate(unary.Argument, scope);
					return JsValue.Undefined;
				case "delete":
					if (unary.Argument is MemberExpression member)
					{
						JsValue target = Evaluate(member.Object, scope);
						string key = MemberKey(member, scope);
						return !target.IsObject || target.Object.Delete(key);
					}
					return unary.Argument is not Identifier;
				default:
					throw SyntaxErrorThrow("unknown operator " + unary.Operator);
			}
		}

		private JsValue EvaluateUpdate(UpdateExpression update, Scope scope)
		{
			double delta = update.Operator == "++" ? 1 : -1;
			if (update.Argument is MemberExpression member)
			{
				JsValue target = Evaluate(member.Object, scope);
				string key = MemberKey(member, scope);
				double old = GetMember(target, key).ToNumber();
				SetMember(target, key, old + delta);
				return update.Prefix ? old + delta : old;
			}
			if (update.Argument is Identifier id)
			{
				double old = LookupIdentifier(id.Name, scope).ToNumber();
				AssignIdentifier(id.Name, old + delta, scope);
				return update.Prefix ? old + delta : old;
			}
			throw SyntaxErrorThrow("Invalid left-hand side expression in " + (update.Prefix ? "prefix" : "postfix") + " operation");
		}

		public JsValue BinaryOperation(string op, JsValue left, JsValue right)
		{
			switch (op)
			{
				case "+":
				{
					JsValue l = ToPrimitiveDefault(left);
					JsValue r = ToPrimitiveDefault(right);
					if (l.IsString || r.IsString)
						return string.Concat(l.ToJsString(), r.ToJsString());
					return l.ToNumber() + r.ToNumber();
				}
				case "-":
					return left.ToNumber() - right.ToNumber();
				case "*":
					return left.ToNumber() * right.ToNumber();
				case "/":
					return left.ToNumber() / right.ToNumber();
				case "%":
					return left.ToNumber() % right.ToNumber();
				case "**":
				{
					double exponent = right.ToNumber();
					return double.IsNaN(exponent) ? double.NaN : Math.Pow(left.ToNumber(), exponent);
				}
				case "==":
					return JsValue.LooseEquals(left, right);
				case "!=":
					return !JsValue.LooseEquals(left, right);
				case "===":
					return JsValue.StrictEquals(left, right);
				case "!==":
					return !JsValue.StrictEquals(left, right);
				case "<":
					return LessThan(left, right, false);
				case ">":
					return LessThan(right, left, false);
				case "<=":
					return LessThan(left, right, true);
				case ">=":
					return LessThan(right, left, true);
				case "&":
					return left.ToInt32() & right.ToInt32();
				case "|":
					return left.ToInt32() | right.ToInt32();
				case "^":
					return left.ToInt32() ^ right.ToInt32();
				case "<<":
					return left.ToInt32() << (int)(right.ToUInt32() & 31);
				case ">>":
					return left.ToInt32() >> (int)(right.ToUInt32() & 31);
				case ">>>":
					return (double)(left.ToUInt32() >> (int)(right.ToUInt32() & 31));
				case "instanceof":
					return InstanceOf(left, right);
				case "in":
					if (!right.IsObject)
						throw TypeError("Cannot use 'in' operator to search for '" + left.ToJsString() + "' in " + right.ToJsString());
					return right.Object.HasProperty(ToPropertyKey(left));
				default:
					throw SyntaxErrorThrow("unknown operator " + op);
			}
		}

		// Abstract relational comparison: strings by code unit, anything else as numbers, false whenever NaN is involved
		private static bool LessThan(JsValue left, JsValue right, bool orEqual)
		{
			JsValue l = left.ToPrimitive(true);
			JsValue r = right.ToPrimitive(true);
			if (l.IsString && r.IsString)
			{
				int c = string.CompareOrdinal(l.String, r.String);
				return orEqual ? c <= 0 : c < 0;
			}
			double a = l.ToNumber();
			double b = r.ToNumber();
			if (double.IsNaN(a) || double.IsNaN(b))
				return false;
			return orEqual ? a <= b : a < b;
		}

		// ToPrimitive with no hint: dates prefer strings, everything else numbers
		public static JsValue ToPrimitiveDefault(JsValue value)
		{
			if (!value.IsObject)
				return value;
			return value.Object.ToPrimitive(value.Object is not JsPrimitiveObject { Class: "Date" });
		}

		public bool InstanceOf(JsValue left, JsValue right)
		{
			if (!right.IsObject)
				throw TypeError("Right-hand side of 'instanceof' is not an object");
			JsProperty custom = right.Object.GetProperty(SymbolHasInstance);
			if (custom != null)
			{
				JsValue method = right.Object.Get(SymbolHasInstance, this, right);
				if (method.IsCallable)
					return Call(method.Function, right, new[] { left }).Truthy;
			}
			if (!right.IsCallable)
				throw TypeError("Right-hand side of 'instanceof' is not callable");
			JsFunction function = right.Function;
			if (function.Target != null)
				return InstanceOf(left, function.Target);
			if (!left.IsObject)
				return false;
			JsValue prototype = GetMember(right, "prototype");
			if (!prototype.IsObject)
				throw TypeError("Function has non-object prototype in instanceof check");
			for (JsObject o = left.Object.Prototype; o != null; o = o.Prototype)
			{
				if (o == prototype.Object)
					return true;
			}
			return false;
		}

		private JsValue EvaluateTemplate(TemplateLiteral template, Scope scope)
		{
			StringBuilder builder = new StringBuilder(template.Strings[0]);
			for (int i = 0; i < template.Expressions.Count; i++)
			{
				builder.Append(Evaluate(template.Expressions[i], scope).ToJsString());
				builder.Append(template.Strings[i + 1]);
			}
			return builder.ToString();
		}

		private JsValue EvaluateTaggedTemplate(TemplateLiteral template, Scope scope)
		{
			JsValue thisValue = JsValue.Undefined;
			JsValue tag;
			if (template.Tag is MemberExpression member && member.Object is not SuperExpression)
			{
				thisValue = Evaluate(member.Object, scope);
				tag = GetMember(thisValue, MemberKey(member, scope));
			}
			else
				tag = Evaluate(template.Tag, scope);
			if (!tag.IsCallable)
				throw TypeError(Describe(template.Tag) + " is not a function");
			JsArray strings = NewArray();
			JsArray raw = NewArray();
			foreach (string s in template.Strings)
			{
				strings.Elements.Add(s);
				raw.Elements.Add(s);
			}
			strings.Put("raw", raw, false);
			JsValue[] arguments = new JsValue[template.Expressions.Count + 1];
			arguments[0] = strings;
			for (int i = 0; i < template.Expressions.Count; i++)
				arguments[i + 1] = Evaluate(template.Expressions[i], scope);
			return Call(tag.Function, thisValue, arguments);
		}

		private JsValue EvaluateArrayLiteral(ArrayLiteral literal, Scope scope)
		{
			JsArray array = NewArray();
			foreach (Expression element in literal.Elements)
			{
				if (element == null)
					array.Elements.Add(JsValue.Undefined);
				else if (element is SpreadElement spread)
					array.Elements.AddRange(Iterate(Evaluate(spread.Argument, scope)));
				else
					array.Elements.Add(Evaluate(element, scope));
			}
			return array;
		}

		private JsValue EvaluateObjectLiteral(ObjectLiteral literal, Scope scope)
		{
			JsObject obj = NewObject();
			foreach (ObjectProperty property in literal.Properties)
			{
				if (property.IsSpread)
				{
					CopyDataProperties(obj, Evaluate(property.Value, scope));
					continue;
				}
				string key = property.ComputedKey != null ? ToPropertyKey(Evaluate(property.ComputedKey, scope)) : property.Key;
				if (property.Getter != null || property.Setter != null)
				{
					JsFunction getter = property.Getter != null ? MakeFunction(property.Getter, scope, obj) : null;
					JsFunction setter = property.Setter != null ? MakeFunction(property.Setter, scope, obj) : null;
					JsProperty existing = obj.GetOwnProperty(key);
					if (existing != null && existing.IsAccessor)
					{
						existing.Getter ??= getter;
						existing.Setter ??= setter;
					}
					else
						obj.DefineAccessor(key, getter, setter, true);
					continue;
				}
				if (key == "__proto__" && property.ComputedKey == null && !property.IsShorthandMethod && property.Value is not Identifier { Name: "__proto__" })
				{
					JsValue prototype = Evaluate(property.Value, scope);
					if (prototype.IsObject)
						obj.Prototype = prototype.Object;
					else if (prototype.IsNull)
						obj.Prototype = null;
					continue;
				}
				JsValue value;
				if (property.IsShorthandMethod)
					value = MakeFunction(((FunctionExpression)property.Value).Function, scope, obj);
				else
				{
					value = Evaluate(property.Value, scope);
					if (value.IsCallable && value.Function.Name == "" && (property.Value is FunctionExpression || property.Value is ClassNode))
						value.Function.Name = key;
				}
				obj.Put(key, value);
			}
			return obj;
		}

		// Copies own enumerable properties, as object spread and Object.assign do
		public void CopyDataProperties(JsObject target, JsValue source)
		{
			if (source.IsNullish)
				return;
			if (source.IsString)
			{
				string s = source.String;
				for (int i = 0; i < s.Length; i++)
					target.Put(i.ToString(System.Globalization.CultureInfo.InvariantCulture), s[i].ToString());
				return;
			}
			if (!source.IsObject)
				return;
			foreach (string key in OwnEnumerableKeys(source.Object, true))
				target.Set(key, source.Object.Get(key, this, source), this);
		}

		// Functions

		private JsValue EvaluateFunctionExpression(FunctionNode node, Scope scope)
		{
			if (node.Name != null && !node.IsArrow && !node.IsMethod)
			{
				// A named function expression sees its own name inside the body
				Scope inner = new Scope(scope, false);
				JsFunction function = MakeFunction(node, inner);
				inner.Declare(node.Name, function, false);
				return function;
			}
			return MakeFunction(node, scope);
		}

		public JsFunction MakeFunction(FunctionNode node, Scope closure, JsObject homeObject = null)
		{
			JsFunction function = new JsFunction(FunctionPrototype)
			{
				Name = node.Name ?? "",
				Node = node,
				Closure = closure,
				IsArrow = node.IsArrow,
				HomeObject = homeObject,
			};
			int length = 0;
			while (length < node.Parameters.Count && node.Defaults[length] == null && node.Parameters[length] is Identifier)
				length++;
			function.ExpectedLength = length;
			if (node.IsGenerator)
			{
				JsObject prototype = new JsObject(GeneratorPrototype);
				function.Put("prototype", prototype, false);
			}
			return function;
		}

		// The prototype object of an ordinary script function, made when first asked for
		private JsObject EnsurePrototype(JsFunction function)
		{
			JsProperty own = function.GetOwnProperty("prototype");
			if (own != null)
				return own.Value.IsObject ? own.Value.Object : null;
			if (!function.IsConstructor || function.Node == null)
				return null;
			JsObject prototype = NewObject();
			prototype.Put("constructor", function, false);
			function.Put("prototype", prototype, false);
			return prototype;
		}

		public JsValue Call(JsValue callee, JsValue thisValue, JsValue[] arguments)
		{
			if (!callee.IsCallable)
				throw TypeError(callee.ToJsString() + " is not a function");
			return Call(callee.Function, thisValue, arguments);
		}

		public JsValue Call(JsFunction function, JsValue thisValue, JsValue[] arguments)
		{
			if (Current != this)
			{
				Interpreter prior = Current;
				Current = this;
				try
				{
					return Call(function, thisValue, arguments);
				}
				finally
				{
					Current = prior;
				}
			}
			if (function.Target != null)
				return Call(function.Target, function.BoundThis, Concat(function.BoundArguments, arguments));
			if (function.Native != null)
				return function.Native(this, thisValue, arguments);
			if (function.IsClassConstructor)
				throw TypeError($"Class constructor {function.Name} cannot be invoked without 'new'");
			if (function.Node.IsGenerator)
				return CreateGenerator(function, thisValue, arguments);
			if (function.Node.IsAsync)
				return CallAsync(function, thisValue, arguments);
			return RunBody(function, NewFunctionScope(function, thisValue, JsValue.Undefined), arguments);
		}

		private static JsValue[] Concat(JsValue[] first, JsValue[] second)
		{
			if (first == null || first.Length == 0)
				return second;
			JsValue[] result = new JsValue[first.Length + second.Length];
			first.CopyTo(result, 0);
			second.CopyTo(result, first.Length);
			return result;
		}

		private Scope NewFunctionScope(JsFunction function, JsValue thisValue, JsValue newTarget)
		{
			Scope scope = new Scope(function.Closure, true);
			if (function.IsArrow)
				return scope;
			scope.HasThis = true;
			scope.FunctionObject = function;
			scope.NewTarget = newTarget;
			scope.HomeObject = function.HomeObject;
			// Sloppy functions see the global object for a missing this; methods and strict code see what they were given
			scope.ThisValue = thisValue.IsNullish && !function.Node.Strict && function.HomeObject == null ? Global : thisValue;
			return scope;
		}

		internal JsValue RunBody(JsFunction function, Scope scope, JsValue[] arguments)
		{
			if (_depth >= MaxDepth)
				throw RangeError("Maximum call stack size exceeded");
			try
			{
				System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
			}
			catch (InsufficientExecutionStackException)
			{
				throw RangeError("Maximum call stack size exceeded");
			}
			_depth++;
			(_frames ??= new List<Frame>()).Add(new Frame { Name = function.Name, CallLine = CurrentLine });
			try
			{
				FunctionNode node = function.Node;
				BindParameters(node, scope, arguments);
				if (node.UsesArguments && !function.IsArrow)
					scope.Declare("arguments", NewArguments(arguments));
				if (node.ExpressionBody != null)
					return Evaluate(node.ExpressionBody, scope);
				HoistVars(node.Body.Body, scope, false);
				HoistLexical(node.Body.Body, scope, false);
				foreach (Statement statement in node.Body.Body)
				{
					Completion completion = Execute(statement, scope);
					if (completion.Type == CompletionType.Return)
						return completion.Value;
					if (completion.IsAbrupt)
						break;
				}
				return JsValue.Undefined;
			}
			finally
			{
				_depth--;
				_frames.RemoveAt(_frames.Count - 1);
			}
		}

		private void BindParameters(FunctionNode node, Scope scope, JsValue[] arguments)
		{
			for (int i = 0; i < node.Parameters.Count; i++)
			{
				JsValue value = i < arguments.Length ? arguments[i] : JsValue.Undefined;
				if (value.IsUndefined && node.Defaults[i] != null)
					value = EvaluateNamed(node.Defaults[i], scope, node.Parameters[i] as Identifier);
				BindPattern(node.Parameters[i], value, scope, DeclarationKind.Let);
			}
			if (node.RestParameter != null)
			{
				JsArray rest = NewArray();
				for (int i = node.Parameters.Count; i < arguments.Length; i++)
					rest.Elements.Add(arguments[i]);
				BindPattern(node.RestParameter, rest, scope, DeclarationKind.Let);
			}
		}

		private JsArray NewArguments(JsValue[] arguments)
		{
			JsArray array = new JsArray(ObjectPrototype, new List<JsValue>(arguments)) { Class = "Arguments" };
			array.Put(SymbolIterator, ArrayValues, false);
			return array;
		}

		public JsValue Construct(JsValue callee, JsValue[] arguments) => Construct(callee, arguments, callee);

		public JsValue Construct(JsValue callee, JsValue[] arguments, JsValue newTarget)
		{
			if (!callee.IsCallable || !callee.Function.IsConstructor)
				throw TypeError(callee.ToJsString() + " is not a constructor");
			if (Current != this)
			{
				Interpreter prior = Current;
				Current = this;
				try
				{
					return Construct(callee, arguments, newTarget);
				}
				finally
				{
					Current = prior;
				}
			}
			JsFunction function = callee.Function;
			if (function.Target != null)
				return Construct(function.Target, Concat(function.BoundArguments, arguments), ReferenceEquals(newTarget.Object, function) ? function.Target : newTarget);
			if (function.Native != null)
				return function.NativeNew(this, arguments, newTarget.Object);
			if (!function.IsDerivedConstructor)
			{
				JsObject instance = new JsObject(PrototypeFromConstructor(newTarget.Object, ObjectPrototype));
				InitializeFields(function, instance);
				if (function.Node == null)
					return instance;
				JsValue result = RunBody(function, NewFunctionScope(function, instance, newTarget), arguments);
				return result.IsObject ? result : instance;
			}
			JsValue parent = function.Prototype;
			if (function.Node == null)
			{
				// The default derived constructor forwards its arguments to the parent
				JsValue instance = Construct(parent, arguments, newTarget);
				InitializeFields(function, instance.Object);
				return instance;
			}
			Scope scope = NewFunctionScope(function, JsValue.Undefined, newTarget);
			scope.ThisPending = true;
			JsValue returned = RunBody(function, scope, arguments);
			if (returned.IsObject)
				return returned;
			if (scope.ThisPending)
				throw ReferenceError("Must call super constructor in derived class before accessing 'this' or returning from derived constructor");
			return scope.ThisValue;
		}

		public JsObject PrototypeFromConstructor(JsObject newTarget, JsObject fallback)
		{
			if (newTarget == null)
				return fallback;
			JsValue prototype = GetMember(newTarget, "prototype");
			return prototype.IsObject ? prototype.Object : fallback;
		}

		private JsValue SuperCall(CallExpression call, Scope scope)
		{
			Scope functionScope = scope.ThisScope();
			JsFunction constructor = functionScope?.FunctionObject;
			if (constructor == null || !constructor.IsDerivedConstructor)
				throw SyntaxErrorThrow("'super' keyword unexpected here");
			if (!functionScope.ThisPending)
				throw ReferenceError("Super constructor may only be called once");
			JsValue[] arguments = EvaluateArguments(call.Arguments, scope);
			JsValue parent = constructor.Prototype;
			if (!parent.IsCallable || !parent.Function.IsConstructor)
				throw TypeError("Super constructor is not a constructor");
			JsValue instance = Construct(parent, arguments, functionScope.NewTarget);
			functionScope.ThisValue = instance;
			functionScope.ThisPending = false;
			InitializeFields(constructor, instance.Object);
			return instance;
		}

		private JsValue ResolveThis(Scope scope)
		{
			Scope thisScope = scope.ThisScope();
			if (thisScope == null)
				return JsValue.Undefined;
			if (thisScope.ThisPending)
				throw ReferenceError("Must call super constructor in derived class before accessing 'this' or returning from derived constructor");
			return thisScope.ThisValue;
		}

		private static JsObject HomeOf(Scope scope) => scope.ThisScope()?.HomeObject;

		// Classes

		private JsFunction EvaluateClass(ClassNode node, Scope scope)
		{
			JsObject prototypeParent = ObjectPrototype;
			JsObject constructorParent = FunctionPrototype;
			bool derived = false;
			if (node.SuperClass != null)
			{
				JsValue parent = Evaluate(node.SuperClass, scope);
				derived = true;
				if (parent.IsNull)
					prototypeParent = null;
				else
				{
					if (!parent.IsCallable || !parent.Function.IsConstructor)
						throw TypeError("Class extends value " + parent.ToJsString() + " is not a constructor or null");
					JsValue parentPrototype = GetMember(parent, "prototype");
					if (!parentPrototype.IsObject && !parentPrototype.IsNull)
						throw TypeError("Class extends value does not have valid prototype property");
					prototypeParent = parentPrototype.IsObject ? parentPrototype.Object : null;
					constructorParent = parent.Object;
				}
			}
			Scope classScope = new Scope(scope, false);
			if (node.Name != null)
				classScope.Declare(node.Name, JsValue.Undefined, false, false);
			JsObject prototype = new JsObject(prototypeParent);
			JsFunction constructor = node.Constructor != null ? MakeFunction(node.Constructor, classScope, prototype) : new JsFunction(FunctionPrototype) { Closure = classScope, HomeObject = prototype };
			constructor.Prototype = constructorParent;
			constructor.Name = node.Name ?? "";
			constructor.IsClassConstructor = true;
			constructor.IsDerivedConstructor = derived;
			constructor.ClassNode = node;
			constructor.ClassScope = classScope;
			constructor.DefineOwn("prototype", new JsProperty { Value = prototype, Enumerable = false, Writable = false, Configurable = false });
			prototype.Put("constructor", constructor, false);
			List<ClassMember> staticInitializers = new List<ClassMember>();
			foreach (ClassMember member in node.Members)
			{
				JsObject target = member.IsStatic ? constructor : prototype;
				if (member.IsField)
				{
					if (member.IsStatic)
						staticInitializers.Add(member);
					continue;
				}
				string key = member.ComputedName != null ? ToPropertyKey(Evaluate(member.ComputedName, classScope)) : member.Name;
				JsFunction function = MakeFunction(member.Function, classScope, target);
				if (member.IsGetter || member.IsSetter)
				{
					function.Name = (member.IsGetter ? "get " : "set ") + key;
					JsProperty existing = target.GetOwnProperty(key);
					if (existing != null && existing.IsAccessor)
					{
						if (member.IsGetter)
							existing.Getter = function;
						else
							existing.Setter = function;
					}
					else
						target.DefineAccessor(key, member.IsGetter ? function : null, member.IsSetter ? function : null);
				}
				else
				{
					function.Name = key;
					target.Put(key, function, false);
				}
			}
			if (node.Name != null)
				InitializeLexical(classScope, node.Name, constructor, false);
			foreach (ClassMember member in staticInitializers)
			{
				Scope initializer = new Scope(classScope, true) { HasThis = true, ThisValue = constructor, HomeObject = constructor, FunctionObject = constructor };
				if (member.Function != null)
				{
					RunBody(MakeFunction(member.Function, classScope, constructor), initializer, Array.Empty<JsValue>());
					continue;
				}
				string key = member.ComputedName != null ? ToPropertyKey(Evaluate(member.ComputedName, classScope)) : member.Name;
				JsValue value = member.Value == null ? JsValue.Undefined : Evaluate(member.Value, initializer);
				if (value.IsCallable && value.Function.Name == "" && (member.Value is FunctionExpression || member.Value is ClassNode))
					value.Function.Name = key;
				constructor.Put(key, value);
			}
			return constructor;
		}

		// Instance fields run in order against the new object, before the constructor body sees it
		private void InitializeFields(JsFunction constructor, JsObject instance)
		{
			if (constructor.ClassNode == null)
				return;
			Scope scope = null;
			foreach (ClassMember member in constructor.ClassNode.Members)
			{
				if (!member.IsField || member.IsStatic)
					continue;
				scope ??= new Scope(constructor.ClassScope, true) { HasThis = true, ThisValue = instance, HomeObject = constructor.HomeObject, FunctionObject = constructor };
				string key = member.ComputedName != null ? ToPropertyKey(Evaluate(member.ComputedName, scope)) : member.Name;
				JsValue value = member.Value == null ? JsValue.Undefined : Evaluate(member.Value, scope);
				if (value.IsCallable && value.Function.Name == "" && (member.Value is FunctionExpression || member.Value is ClassNode))
					value.Function.Name = key;
				instance.DefineOwn(key, new JsProperty { Value = value });
			}
		}

		// Property access on any value

		public JsValue GetMember(JsValue target, string key)
		{
			switch (target.Type)
			{
				case JsType.Object:
				{
					JsObject obj = target.Object;
					if (obj is JsFunction function)
					{
						if (key == "prototype" && function.Node != null)
						{
							JsObject prototype = EnsurePrototype(function);
							if (prototype == null && function.GetOwnProperty("prototype") == null)
								return JsValue.Undefined;
						}
						else if (key == "name" && function.GetOwnProperty(key) == null)
							return function.Name;
						else if (key == "length" && function.GetOwnProperty(key) == null)
							return function.ExpectedLength;
					}
					else if (obj is JsPrimitiveObject { Primitive: { IsString: true } } wrapper)
					{
						string s = wrapper.Primitive.String;
						if (key == "length")
							return s.Length;
						if (JsArray.TryIndex(key, out int index))
							return index < s.Length ? s[index].ToString() : JsValue.Undefined;
					}
					return obj.Get(key, this, target);
				}
				case JsType.String:
				{
					string s = target.String;
					if (key == "length")
						return s.Length;
					if (JsArray.TryIndex(key, out int index))
						return index < s.Length ? s[index].ToString() : JsValue.Undefined;
					return StringPrototype.Get(key, this, target);
				}
				case JsType.Number:
					return NumberPrototype.Get(key, this, target);
				case JsType.Boolean:
					return BooleanPrototype.Get(key, this, target);
				default:
					throw TypeError($"Cannot read properties of {target.ToJsString()} (reading '{key}')");
			}
		}

		public JsValue GetMember(JsObject target, string key) => GetMember(JsValue.FromObject(target), key);

		public void SetMember(JsValue target, string key, JsValue value)
		{
			if (target.IsObject)
			{
				target.Object.Set(key, value, this, target);
				return;
			}
			if (target.IsNullish)
				throw TypeError($"Cannot set properties of {target.ToJsString()} (setting '{key}')");
		}

		public string ToPropertyKey(JsValue value)
		{
			if (value.IsString)
				return value.String;
			if (value.IsNumber)
				return JsValue.NumberToString(value.Number);
			if (value.IsSymbol)
				return value.Symbol.Key;
			if (value.IsObject)
			{
				JsValue primitive = value.Object.ToPrimitive(false);
				return primitive.IsSymbol ? primitive.Symbol.Key : primitive.ToJsString();
			}
			return value.ToJsString();
		}

		public JsObject ToObject(JsValue value)
		{
			switch (value.Type)
			{
				case JsType.Object:
					return value.Object;
				case JsType.String:
					return new JsPrimitiveObject(StringPrototype, value, "String");
				case JsType.Number:
					return new JsPrimitiveObject(NumberPrototype, value, "Number");
				case JsType.Boolean:
					return new JsPrimitiveObject(BooleanPrototype, value, "Boolean");
				default:
					throw TypeError("Cannot convert undefined or null to object");
			}
		}

		// A property key scripts can see: not a symbol, not a private name
		public static bool IsVisibleKey(string key) => !(key.Length > 1 && key[0] == '@' && key[1] == '@') && !(key.Length > 0 && key[0] == '#');

		// Own enumerable string keys: integer keys ascending, then the rest in insertion order
		public List<string> OwnEnumerableKeys(JsObject obj, bool includeSymbols = false)
		{
			List<string> keys = new List<string>();
			if (obj is JsArray array)
				keys.AddRange(array.IndexKeys());
			else if (obj is JsPrimitiveObject { Primitive: { IsString: true } } wrapper)
			{
				for (int i = 0; i < wrapper.Primitive.String.Length; i++)
					keys.Add(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
			}
			List<int> indices = null;
			List<string> names = null;
			foreach (string key in obj.OwnKeys)
			{
				JsProperty property = obj.GetOwnProperty(key);
				if (property == null || !property.Enumerable)
					continue;
				if (!IsVisibleKey(key))
				{
					if (includeSymbols && key[0] == '@')
						(names ??= new List<string>()).Add(key);
					continue;
				}
				if (obj is not JsArray && JsArray.TryIndex(key, out int index))
					(indices ??= new List<int>()).Add(index);
				else
					(names ??= new List<string>()).Add(key);
			}
			if (indices != null)
			{
				indices.Sort();
				foreach (int index in indices)
					keys.Add(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
			}
			if (names != null)
				keys.AddRange(names);
			return keys;
		}

		// Keys for for-in: own enumerable keys, then the prototype chain's, each once
		public List<string> EnumerableKeys(JsObject obj)
		{
			List<string> keys = new List<string>();
			HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
			for (JsObject o = obj; o != null; o = o.Prototype)
			{
				foreach (string key in OwnEnumerableKeys(o))
				{
					if (seen.Add(key))
						keys.Add(key);
				}
				foreach (string key in o.OwnKeys)
					seen.Add(key);
			}
			return keys;
		}

		// Iteration protocol: arrays and strings directly, anything else through Symbol.iterator
		public IEnumerable<JsValue> Iterate(JsValue iterable)
		{
			if (iterable.IsObject && iterable.Object is JsArray array && IsPlainArrayIteration(array))
			{
				for (int i = 0; i < array.Elements.Count; i++)
					yield return array.Elements[i];
				yield break;
			}
			if (iterable.IsString)
			{
				string s = iterable.String;
				for (int i = 0; i < s.Length; i++)
				{
					if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
					{
						yield return s.Substring(i, 2);
						i++;
					}
					else
						yield return s[i].ToString();
				}
				yield break;
			}
			JsValue method = iterable.IsNullish ? JsValue.Undefined : GetMember(iterable, SymbolIterator);
			if (!method.IsCallable)
				throw TypeError(iterable.ToJsString() + " is not iterable");
			JsValue iterator = Call(method.Function, iterable, Array.Empty<JsValue>());
			if (!iterator.IsObject)
				throw TypeError("Result of the Symbol.iterator method is not an object");
			JsValue next = GetMember(iterator, "next");
			bool done = true;
			try
			{
				while (true)
				{
					done = true;
					JsValue result = Call(next, iterator, Array.Empty<JsValue>());
					if (!result.IsObject)
						throw TypeError("Iterator result " + result.ToJsString() + " is not an object");
					if (GetMember(result, "done").Truthy)
						break;
					done = false;
					yield return GetMember(result, "value");
				}
			}
			finally
			{
				if (!done)
				{
					JsValue close = GetMember(iterator, "return");
					if (close.IsCallable)
						Call(close.Function, iterator, Array.Empty<JsValue>());
				}
			}
		}

		private bool IsPlainArrayIteration(JsArray array)
		{
			JsProperty property = array.GetProperty(SymbolIterator);
			return property != null && !property.IsAccessor && property.Value.IsObject && ReferenceEquals(property.Value.Object, ArrayValues);
		}

		// Errors

		public JsObject NewError(string type, string message)
		{
			JsObject prototype = ErrorPrototypes.TryGetValue(type, out JsObject p) ? p : ErrorPrototype;
			JsObject error = new JsObject(prototype) { Class = "Error" };
			error.Put("message", message, false);
			error.Put("stack", type + ": " + message + StackTrace(), false);
			return error;
		}

		public string StackTrace()
		{
			StringBuilder builder = new StringBuilder();
			int line = CurrentLine;
			_frames ??= new List<Frame>();
			for (int i = _frames.Count - 1; i >= 0 && _frames.Count - i <= 20; i--)
			{
				builder.Append("\n    at ").Append(_frames[i].Name.Length == 0 ? "<anonymous>" : _frames[i].Name).Append(" (script:").Append(line).Append(')');
				line = _frames[i].CallLine;
			}
			builder.Append("\n    at <script> (script:").Append(line).Append(')');
			return builder.ToString();
		}

		public JsThrow TypeError(string message) => new JsThrow(NewError("TypeError", message));

		public JsThrow RangeError(string message) => new JsThrow(NewError("RangeError", message));

		public JsThrow ReferenceError(string message) => new JsThrow(NewError("ReferenceError", message));

		public JsThrow SyntaxErrorThrow(string message) => new JsThrow(NewError("SyntaxError", message));

		// A readable rendering of a value, as console.log shows it
		public string ToDisplayString(JsValue value, int depth = 0)
		{
			if (value.IsString)
				return depth == 0 ? value.String : "'" + value.String + "'";
			if (!value.IsObject)
				return value.ToJsString();
			JsObject obj = value.Object;
			if (obj is JsFunction function)
				return (function.IsClassConstructor ? "[class " : "[Function: ") + (function.Name.Length == 0 ? "(anonymous)" : function.Name) + "]";
			if (obj.Class == "Error")
				return DescribeError(value);
			if (obj is JsSymbol || obj.Class == "Date" || obj.Class == "RegExp")
				return obj.ToPrimitive(false).ToJsString();
			if (depth > 2)
				return obj is JsArray ? "[Array]" : "[Object]";
			StringBuilder builder = new StringBuilder();
			if (obj is JsArray array)
			{
				builder.Append('[');
				for (int i = 0; i < array.Elements.Count && i < 100; i++)
					builder.Append(i == 0 ? " " : ", ").Append(ToDisplayString(array.Elements[i], depth + 1));
				if (array.Elements.Count > 100)
					builder.Append(", ... ").Append(array.Elements.Count - 100).Append(" more");
				return builder.Append(array.Elements.Count > 0 ? " ]" : "]").ToString();
			}
			if (obj.Class == "Map" || obj.Class == "Set")
				return obj.Class + "(" + GetMember(value, "size").ToJsString() + ")";
			builder.Append('{');
			int count = 0;
			foreach (string key in OwnEnumerableKeys(obj))
			{
				if (count++ > 0)
					builder.Append(',');
				builder.Append(' ').Append(key).Append(": ").Append(ToDisplayString(obj.Get(key, this, value), depth + 1));
			}
			return builder.Append(count > 0 ? " }" : "}").ToString();
		}
	}
}
