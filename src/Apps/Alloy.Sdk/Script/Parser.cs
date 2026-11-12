using System;
using System.Collections.Generic;

namespace Alloy.Sdk.Script
{
	// A recursive-descent parser for the JavaScript pages use: ES5 plus the ES2015+ forms seen in the wild, with semicolon insertion
	public sealed class Parser
	{
		private readonly List<Token> _tokens;
		private int _at;
		private int _functionDepth;
		private bool _inAsync;
		private bool _inGenerator;
		private readonly List<FunctionNode> _functions = new List<FunctionNode>();

		public Parser(string source)
		{
			_tokens = new Lexer(source).Tokenize();
		}

		public static Program Parse(string source) => new Parser(source).ParseProgram();

		public static FunctionNode ParseFunctionBody(string parameters, string body)
		{
			Parser parser = new Parser("(function anonymous(" + parameters + "\n) {\n" + body + "\n})");
			Program program = parser.ParseProgram();
			return ((FunctionExpression)((ExpressionStatement)program.Body[0]).Expression).Function;
		}

		private Token Peek(int offset = 0) => _tokens[Math.Min(_at + offset, _tokens.Count - 1)];

		private Token Current => _tokens[_at];

		private Token Advance() => _tokens[_at++];

		private bool IsPunctuator(string text) => Current.Kind == TokenKind.Punctuator && Current.Text == text;

		private bool IsKeyword(string text) => Current.Kind == TokenKind.Keyword && Current.Text == text;

		private bool IsIdentifierLike => Current.Kind == TokenKind.Identifier || (Current.Kind == TokenKind.Keyword && IsContextualKeyword(Current.Text));

		// Words that are keywords only in context can still name things
		private static bool IsContextualKeyword(string text) => text is "of" or "get" or "set" or "static" or "async" or "await" or "yield" or "let" or "undefined";

		private bool Match(string punctuator)
		{
			if (!IsPunctuator(punctuator))
				return false;
			_at++;
			return true;
		}

		private bool MatchKeyword(string keyword)
		{
			if (!IsKeyword(keyword))
				return false;
			_at++;
			return true;
		}

		private Token Expect(string punctuator)
		{
			if (!IsPunctuator(punctuator))
				throw Error($"expected '{punctuator}' but found '{Current.Text}'");
			return Advance();
		}

		private SyntaxError Error(string message) => new SyntaxError(message, Current.Line);

		private string ExpectIdentifier()
		{
			if (!IsIdentifierLike)
				throw Error($"expected a name but found '{Current.Text}'");
			return Advance().Text;
		}

		// Automatic semicolon insertion: a semicolon, a closing brace, the end, or a line break before this token
		private void ConsumeSemicolon()
		{
			if (Match(";"))
				return;
			if (IsPunctuator("}") || Current.Kind == TokenKind.EndOfFile || Current.NewlineBefore)
				return;
			throw Error($"unexpected '{Current.Text}'");
		}

		public Program ParseProgram()
		{
			Program program = new Program();
			while (Current.Kind != TokenKind.EndOfFile)
				program.Body.Add(ParseStatement());
			return program;
		}

		private Statement ParseStatement()
		{
			Token token = Current;
			int line = token.Line;
			Statement statement = ParseStatementInner();
			statement.Line = line;
			return statement;
		}

		private Statement ParseStatementInner()
		{
			if (Current.Kind == TokenKind.Punctuator)
			{
				switch (Current.Text)
				{
					case "{":
						return ParseBlock();
					case ";":
						Advance();
						return new EmptyStatement();
				}
			}
			if (Current.Kind == TokenKind.Keyword)
			{
				switch (Current.Text)
				{
					case "var":
					case "const":
						return ParseVariableStatement();
					case "let":
						if (Peek(1).Kind == TokenKind.Identifier || Peek(1).Kind == TokenKind.Keyword && IsContextualKeyword(Peek(1).Text) || Peek(1).Kind == TokenKind.Punctuator && (Peek(1).Text == "[" || Peek(1).Text == "{"))
							return ParseVariableStatement();
						break;
					case "function":
						return new FunctionDeclaration { Function = ParseFunction(false, false, true) };
					case "async":
						if (Peek(1).Kind == TokenKind.Keyword && Peek(1).Text == "function" && !Peek(1).NewlineBefore)
						{
							Advance();
							return new FunctionDeclaration { Function = ParseFunction(true, false, true) };
						}
						break;
					case "class":
						return new ClassDeclaration { Class = ParseClass(true) };
					case "return":
					{
						Advance();
						ReturnStatement result = new ReturnStatement();
						if (!IsPunctuator(";") && !IsPunctuator("}") && Current.Kind != TokenKind.EndOfFile && !Current.NewlineBefore)
							result.Argument = ParseExpression();
						ConsumeSemicolon();
						return result;
					}
					case "if":
					{
						Advance();
						Expect("(");
						IfStatement result = new IfStatement { Test = ParseExpression() };
						Expect(")");
						result.Consequent = ParseStatement();
						if (MatchKeyword("else"))
							result.Alternate = ParseStatement();
						return result;
					}
					case "for":
						return ParseFor(null);
					case "while":
					{
						Advance();
						Expect("(");
						WhileStatement result = new WhileStatement { Test = ParseExpression() };
						Expect(")");
						result.Body = ParseStatement();
						return result;
					}
					case "do":
					{
						Advance();
						WhileStatement result = new WhileStatement { IsDoWhile = true, Body = ParseStatement() };
						if (!MatchKeyword("while"))
							throw Error("expected 'while'");
						Expect("(");
						result.Test = ParseExpression();
						Expect(")");
						Match(";");
						return result;
					}
					case "break":
					{
						Advance();
						BreakStatement result = new BreakStatement();
						if (IsIdentifierLike && !Current.NewlineBefore)
							result.Label = Advance().Text;
						ConsumeSemicolon();
						return result;
					}
					case "continue":
					{
						Advance();
						ContinueStatement result = new ContinueStatement();
						if (IsIdentifierLike && !Current.NewlineBefore)
							result.Label = Advance().Text;
						ConsumeSemicolon();
						return result;
					}
					case "throw":
					{
						Advance();
						ThrowStatement result = new ThrowStatement { Argument = ParseExpression() };
						ConsumeSemicolon();
						return result;
					}
					case "try":
						return ParseTry();
					case "switch":
						return ParseSwitch();
				}
			}
			if (IsIdentifierLike && Peek(1).Kind == TokenKind.Punctuator && Peek(1).Text == ":")
			{
				string label = Advance().Text;
				Advance();
				Statement body = ParseStatement();
				switch (body)
				{
					case ForStatement f:
						f.Label = label;
						break;
					case ForInStatement fi:
						fi.Label = label;
						break;
					case WhileStatement w:
						w.Label = label;
						break;
					case SwitchStatement sw:
						sw.Label = label;
						break;
				}
				return new LabeledStatement { Label = label, Body = body };
			}
			ExpressionStatement statement = new ExpressionStatement { Expression = ParseExpression() };
			ConsumeSemicolon();
			return statement;
		}

		private BlockStatement ParseBlock()
		{
			BlockStatement block = new BlockStatement { Line = Current.Line };
			Expect("{");
			while (!IsPunctuator("}"))
			{
				if (Current.Kind == TokenKind.EndOfFile)
					throw Error("unexpected end of input in block");
				block.Body.Add(ParseStatement());
			}
			Expect("}");
			return block;
		}

		private VariableDeclaration ParseVariableStatement()
		{
			VariableDeclaration declaration = ParseVariableDeclaration(true);
			ConsumeSemicolon();
			return declaration;
		}

		private VariableDeclaration ParseVariableDeclaration(bool allowIn)
		{
			Token keyword = Advance();
			VariableDeclaration declaration = new VariableDeclaration
			{
				Kind = keyword.Text == "var" ? DeclarationKind.Var : keyword.Text == "let" ? DeclarationKind.Let : DeclarationKind.Const,
				Line = keyword.Line,
			};
			do
			{
				VariableDeclarator declarator = new VariableDeclarator { Target = ParseBindingTarget() };
				if (Match("="))
					declarator.Init = ParseAssignment(allowIn);
				declaration.Declarations.Add(declarator);
			}
			while (Match(","));
			return declaration;
		}

		// A binding: a name or a destructuring pattern; in an assignment a member expression can stand where a name would
		private Node ParseBindingTarget()
		{
			if (IsPunctuator("["))
				return ParseArrayPattern();
			if (IsPunctuator("{"))
				return ParseObjectPattern();
			if (IsIdentifierLike && Peek(1).Kind == TokenKind.Punctuator && (Peek(1).Text == "." || Peek(1).Text == "["))
				return ParseLeftHandSide();
			if (IsKeyword("this") && Peek(1).Kind == TokenKind.Punctuator && (Peek(1).Text == "." || Peek(1).Text == "["))
				return ParseLeftHandSide();
			return new Identifier { Name = ExpectIdentifier(), Line = Current.Line };
		}

		private ArrayPattern ParseArrayPattern()
		{
			ArrayPattern pattern = new ArrayPattern { Line = Current.Line };
			Expect("[");
			while (!IsPunctuator("]"))
			{
				if (Match(","))
				{
					pattern.Elements.Add(null);
					pattern.Defaults.Add(null);
					continue;
				}
				if (Match("..."))
				{
					pattern.Rest = ParseBindingTarget();
					Match(",");
					break;
				}
				pattern.Elements.Add(ParseBindingTarget());
				pattern.Defaults.Add(Match("=") ? ParseAssignment(true) : null);
				if (!IsPunctuator("]"))
					Expect(",");
			}
			Expect("]");
			return pattern;
		}

		private ObjectPattern ParseObjectPattern()
		{
			ObjectPattern pattern = new ObjectPattern { Line = Current.Line };
			Expect("{");
			while (!IsPunctuator("}"))
			{
				if (Match("..."))
				{
					pattern.Rest = ParseBindingTarget();
					Match(",");
					break;
				}
				PatternProperty property = new PatternProperty();
				if (Match("["))
				{
					property.ComputedKey = ParseAssignment(true);
					Expect("]");
				}
				else if (Current.Kind == TokenKind.String || Current.Kind == TokenKind.Number)
				{
					Token key = Advance();
					property.Key = key.Kind == TokenKind.String ? key.Value : JsValue.NumberToString(key.Number);
				}
				else
					property.Key = Advance().Text;
				if (Match(":"))
					property.Target = ParseBindingTarget();
				else
					property.Target = new Identifier { Name = property.Key, Line = Current.Line };
				if (Match("="))
					property.Default = ParseAssignment(true);
				pattern.Properties.Add(property);
				if (!IsPunctuator("}"))
					Expect(",");
			}
			Expect("}");
			return pattern;
		}

		private Statement ParseFor(string label)
		{
			Token keyword = Advance();
			bool isAwait = MatchKeyword("await");
			Expect("(");
			Node init = null;
			if (!IsPunctuator(";"))
			{
				int initStart = _at;
				if (IsKeyword("var") || IsKeyword("const") || (IsKeyword("let") && !(Peek(1).Kind == TokenKind.Keyword && Peek(1).Text == "in")))
					init = ParseVariableDeclaration(false);
				else
					init = ParseExpression(false);
				if (IsKeyword("of") || IsKeyword("in"))
				{
					if (init is ArrayLiteral || init is ObjectLiteral)
					{
						// The head is a destructuring assignment target: read it again as a pattern
						_at = initStart;
						init = ParseBindingTarget();
					}
					bool isOf = Advance().Text == "of";
					ForInStatement loop = new ForInStatement { Left = init, IsOf = isOf, Label = label, Line = keyword.Line };
					loop.Right = isOf ? ParseAssignment(true) : ParseExpression();
					Expect(")");
					loop.Body = ParseStatement();
					return loop;
				}
			}
			Expect(";");
			ForStatement result = new ForStatement { Init = init, Label = label, Line = keyword.Line };
			if (!IsPunctuator(";"))
				result.Test = ParseExpression();
			Expect(";");
			if (!IsPunctuator(")"))
				result.Update = ParseExpression();
			Expect(")");
			result.Body = ParseStatement();
			return result;
		}

		private TryStatement ParseTry()
		{
			Advance();
			TryStatement result = new TryStatement { Block = ParseBlock() };
			if (MatchKeyword("catch"))
			{
				if (Match("("))
				{
					result.CatchParameter = ParseBindingTarget();
					Expect(")");
				}
				result.Handler = ParseBlock();
			}
			if (MatchKeyword("finally"))
				result.Finalizer = ParseBlock();
			if (result.Handler == null && result.Finalizer == null)
				throw Error("try without catch or finally");
			return result;
		}

		private SwitchStatement ParseSwitch()
		{
			Advance();
			Expect("(");
			SwitchStatement result = new SwitchStatement { Discriminant = ParseExpression() };
			Expect(")");
			Expect("{");
			while (!IsPunctuator("}"))
			{
				SwitchCase switchCase = new SwitchCase();
				if (MatchKeyword("default"))
					Expect(":");
				else
				{
					if (!MatchKeyword("case"))
						throw Error("expected 'case'");
					switchCase.Test = ParseExpression();
					Expect(":");
				}
				while (!IsPunctuator("}") && !IsKeyword("case") && !IsKeyword("default"))
					switchCase.Body.Add(ParseStatement());
				result.Cases.Add(switchCase);
			}
			Expect("}");
			return result;
		}

		// function [name](params) { body }; the keyword is current unless it was consumed by the caller
		private FunctionNode ParseFunction(bool isAsync, bool isExpression, bool consumeKeyword)
		{
			FunctionNode function = new FunctionNode { IsAsync = isAsync, Line = Current.Line };
			if (consumeKeyword && !MatchKeyword("function"))
				throw Error("expected 'function'");
			if (Match("*"))
				function.IsGenerator = true;
			if (IsIdentifierLike)
				function.Name = Advance().Text;
			else if (!isExpression)
				throw Error("a function declaration needs a name");
			ParseParameters(function);
			function.Body = ParseFunctionBody(function);
			return function;
		}

		private void ParseParameters(FunctionNode function)
		{
			Expect("(");
			while (!IsPunctuator(")"))
			{
				if (Match("..."))
				{
					function.RestParameter = ParseBindingTarget();
					Match(",");
					break;
				}
				function.Parameters.Add(ParseBindingTarget());
				function.Defaults.Add(Match("=") ? ParseAssignment(true) : null);
				if (!IsPunctuator(")"))
					Expect(",");
			}
			Expect(")");
		}

		private BlockStatement ParseFunctionBody(FunctionNode function)
		{
			bool outerAsync = _inAsync;
			bool outerGenerator = _inGenerator;
			_inAsync = function.IsAsync;
			_inGenerator = function.IsGenerator;
			_functionDepth++;
			_functions.Add(function);
			BlockStatement body = ParseBlock();
			_functions.RemoveAt(_functions.Count - 1);
			_functionDepth--;
			_inAsync = outerAsync;
			_inGenerator = outerGenerator;
			if (body.Body.Count > 0 && body.Body[0] is ExpressionStatement { Expression: Literal { Value: { IsString: true } literal } } && literal.String == "use strict")
				function.Strict = true;
			return body;
		}

		private ClassNode ParseClass(bool isDeclaration)
		{
			ClassNode node = new ClassNode { Line = Current.Line };
			if (!MatchKeyword("class"))
				throw Error("expected 'class'");
			if (IsIdentifierLike)
				node.Name = Advance().Text;
			else if (isDeclaration)
				throw Error("a class declaration needs a name");
			if (MatchKeyword("extends"))
				node.SuperClass = ParseLeftHandSide();
			Expect("{");
			while (!IsPunctuator("}"))
			{
				if (Match(";"))
					continue;
				ClassMember member = new ClassMember();
				if (IsKeyword("static") && !(Peek(1).Kind == TokenKind.Punctuator && (Peek(1).Text == "(" || Peek(1).Text == "=")))
				{
					Advance();
					member.IsStatic = true;
					if (IsPunctuator("{"))
					{
						// A static block: run as a method with no name at definition time
						member.Function = new FunctionNode { IsMethod = true, Line = Current.Line, Body = ParseFunctionBody(new FunctionNode()) };
						member.Name = "";
						member.IsField = true;
						node.Members.Add(member);
						continue;
					}
				}
				bool isAsync = false;
				bool isGenerator = false;
				if (IsKeyword("async") && !(Peek(1).Kind == TokenKind.Punctuator && (Peek(1).Text == "(" || Peek(1).Text == "=")) && !Peek(1).NewlineBefore)
				{
					Advance();
					isAsync = true;
				}
				if (Match("*"))
					isGenerator = true;
				if ((IsKeyword("get") || IsKeyword("set")) && !(Peek(1).Kind == TokenKind.Punctuator && (Peek(1).Text == "(" || Peek(1).Text == "=" || Peek(1).Text == ";" || Peek(1).Text == "}")))
				{
					string accessor = Advance().Text;
					member.IsGetter = accessor == "get";
					member.IsSetter = accessor == "set";
				}
				ParsePropertyName(out member.Name, out member.ComputedName);
				if (IsPunctuator("("))
				{
					FunctionNode function = new FunctionNode { IsMethod = true, IsAsync = isAsync, IsGenerator = isGenerator, Name = member.Name, Line = Current.Line };
					ParseParameters(function);
					function.Body = ParseFunctionBody(function);
					member.Function = function;
					if (member.Name == "constructor" && !member.IsStatic && member.ComputedName == null)
					{
						node.Constructor = function;
						continue;
					}
				}
				else
				{
					member.IsField = true;
					if (Match("="))
						member.Value = ParseAssignment(true);
					ConsumeSemicolon();
				}
				node.Members.Add(member);
			}
			Expect("}");
			return node;
		}

		private void ParsePropertyName(out string name, out Expression computed)
		{
			name = null;
			computed = null;
			if (Match("["))
			{
				computed = ParseAssignment(true);
				Expect("]");
				return;
			}
			Token token = Advance();
			switch (token.Kind)
			{
				case TokenKind.String:
					name = token.Value;
					break;
				case TokenKind.Number:
					name = JsValue.NumberToString(token.Number);
					break;
				case TokenKind.Identifier:
				case TokenKind.Keyword:
					name = token.Text;
					break;
				case TokenKind.Punctuator when token.Text == "#":
					name = "#" + Advance().Text;
					break;
				default:
					throw new SyntaxError($"unexpected '{token.Text}' in property name", token.Line);
			}
		}

		// Expressions, from the comma operator down
		public Expression ParseExpression(bool allowIn = true)
		{
			Expression first = ParseAssignment(allowIn);
			if (!IsPunctuator(","))
				return first;
			SequenceExpression sequence = new SequenceExpression { Line = first.Line };
			sequence.Expressions.Add(first);
			while (Match(","))
				sequence.Expressions.Add(ParseAssignment(allowIn));
			return sequence;
		}

		private static readonly HashSet<string> AssignmentOperators = new HashSet<string> { "=", "+=", "-=", "*=", "/=", "%=", "**=", "<<=", ">>=", ">>>=", "&=", "|=", "^=", "&&=", "||=", "??=" };

		private Expression ParseAssignment(bool allowIn)
		{
			if (IsArrowAhead())
				return ParseArrow();
			if (IsKeyword("async") && !Peek(1).NewlineBefore && (Peek(1).Kind == TokenKind.Identifier || Peek(1).Kind == TokenKind.Punctuator && Peek(1).Text == "(") && IsAsyncArrowAhead())
			{
				Advance();
				return ParseArrow(true);
			}
			if (IsKeyword("yield") && _inGenerator)
			{
				Token keyword = Advance();
				YieldExpression yield = new YieldExpression { Line = keyword.Line };
				if (Match("*"))
					yield.Delegate = true;
				if (!IsPunctuator(")") && !IsPunctuator("]") && !IsPunctuator("}") && !IsPunctuator(",") && !IsPunctuator(";") && !Current.NewlineBefore && Current.Kind != TokenKind.EndOfFile)
					yield.Argument = ParseAssignment(allowIn);
				return yield;
			}
			int start = _at;
			Expression left = ParseConditional(allowIn);
			if (Current.Kind == TokenKind.Punctuator && AssignmentOperators.Contains(Current.Text))
			{
				int end = _at;
				Token op = Advance();
				Node target = op.Text == "=" ? ToPattern(left, start, end) : left;
				if (target is not (Identifier or MemberExpression or ArrayPattern or ObjectPattern))
					throw new SyntaxError("invalid assignment target", op.Line);
				return new AssignmentExpression { Operator = op.Text, Target = target, Value = ParseAssignment(allowIn), Line = op.Line };
			}
			return left;
		}

		// An array or object literal on the left of = is a destructuring pattern; it is re-read as one, ending where the = was
		private Node ToPattern(Expression expression, int start, int end)
		{
			if (expression is ArrayLiteral || expression is ObjectLiteral)
			{
				_at = start;
				Node pattern = ParseBindingTarget();
				bool complete = _at == end;
				_at = end + 1;
				if (!complete)
					throw Error("invalid destructuring assignment");
				return pattern;
			}
			return expression;
		}

		private bool IsArrowAhead()
		{
			if (IsIdentifierLike && Peek(1).Kind == TokenKind.Punctuator && Peek(1).Text == "=>" && !Peek(1).NewlineBefore)
				return true;
			if (!IsPunctuator("("))
				return false;
			int depth = 0;
			for (int i = _at; i < _tokens.Count; i++)
			{
				Token token = _tokens[i];
				if (token.Kind == TokenKind.EndOfFile)
					return false;
				if (token.Kind != TokenKind.Punctuator)
					continue;
				if (token.Text == "(" || token.Text == "[" || token.Text == "{")
					depth++;
				else if (token.Text == ")" || token.Text == "]" || token.Text == "}")
				{
					depth--;
					if (depth == 0)
					{
						Token after = _tokens[Math.Min(i + 1, _tokens.Count - 1)];
						return after.Kind == TokenKind.Punctuator && after.Text == "=>" && !after.NewlineBefore;
					}
				}
			}
			return false;
		}

		private bool IsAsyncArrowAhead()
		{
			int save = _at;
			_at++;
			bool arrow = IsArrowAhead();
			_at = save;
			return arrow;
		}

		private Expression ParseArrow(bool isAsync = false)
		{
			FunctionNode function = new FunctionNode { IsArrow = true, IsAsync = isAsync, Line = Current.Line };
			if (IsPunctuator("("))
				ParseParameters(function);
			else
				function.Parameters.Add(new Identifier { Name = ExpectIdentifier(), Line = Current.Line });
			while (function.Defaults.Count < function.Parameters.Count)
				function.Defaults.Add(null);
			Expect("=>");
			bool outerAsync = _inAsync;
			bool outerGenerator = _inGenerator;
			_inAsync = isAsync;
			_inGenerator = false;
			_functions.Add(function);
			if (IsPunctuator("{"))
			{
				_functionDepth++;
				function.Body = ParseBlock();
				_functionDepth--;
			}
			else
				function.ExpressionBody = ParseAssignment(true);
			_functions.RemoveAt(_functions.Count - 1);
			_inAsync = outerAsync;
			_inGenerator = outerGenerator;
			return new FunctionExpression { Function = function, Line = function.Line };
		}

		private Expression ParseConditional(bool allowIn)
		{
			Expression test = ParseBinary(0, allowIn);
			if (!IsPunctuator("?"))
				return test;
			Token question = Advance();
			Expression consequent = ParseAssignment(true);
			Expect(":");
			Expression alternate = ParseAssignment(allowIn);
			return new ConditionalExpression { Test = test, Consequent = consequent, Alternate = alternate, Line = question.Line };
		}

		private static int Precedence(Token token, bool allowIn)
		{
			if (token.Kind == TokenKind.Keyword)
				return token.Text == "instanceof" || (token.Text == "in" && allowIn) ? 10 : -1;
			if (token.Kind != TokenKind.Punctuator)
				return -1;
			return token.Text switch
			{
				"??" => 1,
				"||" => 2,
				"&&" => 3,
				"|" => 4,
				"^" => 5,
				"&" => 6,
				"==" or "!=" or "===" or "!==" => 7,
				"<" or ">" or "<=" or ">=" => 10,
				"<<" or ">>" or ">>>" => 11,
				"+" or "-" => 12,
				"*" or "/" or "%" => 13,
				"**" => 14,
				_ => -1,
			};
		}

		private Expression ParseBinary(int minPrecedence, bool allowIn)
		{
			Expression left = ParseUnary();
			while (true)
			{
				int precedence = Precedence(Current, allowIn);
				if (precedence < 0 || precedence < minPrecedence)
					return left;
				Token op = Advance();
				// ** is right associative; everything else left
				Expression right = op.Text == "**" ? ParseBinary(precedence, allowIn) : ParseBinary(precedence + 1, allowIn);
				if (op.Text == "&&" || op.Text == "||" || op.Text == "??")
					left = new LogicalExpression { Operator = op.Text, Left = left, Right = right, Line = op.Line };
				else
					left = new BinaryExpression { Operator = op.Text, Left = left, Right = right, Line = op.Line };
			}
		}

		private Expression ParseUnary()
		{
			Token token = Current;
			if (token.Kind == TokenKind.Punctuator && (token.Text == "!" || token.Text == "-" || token.Text == "+" || token.Text == "~"))
			{
				Advance();
				return new UnaryExpression { Operator = token.Text, Argument = ParseUnary(), Line = token.Line };
			}
			if (token.Kind == TokenKind.Punctuator && (token.Text == "++" || token.Text == "--"))
			{
				Advance();
				return new UpdateExpression { Operator = token.Text, Prefix = true, Argument = ParseUnary(), Line = token.Line };
			}
			if (token.Kind == TokenKind.Keyword && (token.Text == "typeof" || token.Text == "void" || token.Text == "delete"))
			{
				Advance();
				return new UnaryExpression { Operator = token.Text, Argument = ParseUnary(), Line = token.Line };
			}
			if (token.Kind == TokenKind.Keyword && token.Text == "await" && (_inAsync || _functionDepth == 0))
			{
				Advance();
				return new AwaitExpression { Argument = ParseUnary(), Line = token.Line };
			}
			return ParsePostfix();
		}

		private Expression ParsePostfix()
		{
			Expression expression = ParseLeftHandSide();
			if (Current.Kind == TokenKind.Punctuator && (Current.Text == "++" || Current.Text == "--") && !Current.NewlineBefore)
			{
				Token op = Advance();
				return new UpdateExpression { Operator = op.Text, Prefix = false, Argument = expression, Line = op.Line };
			}
			return expression;
		}

		// Member access, calls, optional chains and new, left to right
		private Expression ParseLeftHandSide()
		{
			Expression expression;
			if (IsKeyword("new"))
			{
				Token keyword = Advance();
				if (Match("."))
				{
					string target = ExpectIdentifier();
					if (target != "target")
						throw Error("new." + target + " is not a thing");
					expression = new NewTargetExpression { Line = keyword.Line };
				}
				else
				{
					Expression callee = ParseNewCallee();
					NewExpression construct = new NewExpression { Callee = callee, Line = keyword.Line };
					if (IsPunctuator("("))
						ParseArguments(construct.Arguments);
					expression = construct;
				}
			}
			else
				expression = ParsePrimary();
			while (true)
			{
				Token token = Current;
				if (token.Kind != TokenKind.Punctuator && token.Kind != TokenKind.Template)
					return expression;
				if (token.Kind == TokenKind.Template)
				{
					TemplateLiteral template = ParseTemplate(Advance());
					template.Tag = expression;
					expression = template;
					continue;
				}
				switch (token.Text)
				{
					case ".":
						Advance();
						expression = new MemberExpression { Object = expression, Name = ParseMemberName(), Line = token.Line };
						break;
					case "?.":
						Advance();
						if (IsPunctuator("("))
						{
							CallExpression call = new CallExpression { Callee = expression, Optional = true, Line = token.Line };
							ParseArguments(call.Arguments);
							expression = call;
						}
						else if (Match("["))
						{
							expression = new MemberExpression { Object = expression, Property = ParseExpression(), Computed = true, Optional = true, Line = token.Line };
							Expect("]");
						}
						else
							expression = new MemberExpression { Object = expression, Name = ParseMemberName(), Optional = true, Line = token.Line };
						break;
					case "[":
						Advance();
						expression = new MemberExpression { Object = expression, Property = ParseExpression(), Computed = true, Line = token.Line };
						Expect("]");
						break;
					case "(":
					{
						CallExpression call = new CallExpression { Callee = expression, Line = token.Line };
						ParseArguments(call.Arguments);
						expression = call;
						break;
					}
					default:
						return expression;
				}
			}
		}

		private string ParseMemberName()
		{
			if (Match("#"))
				return "#" + Advance().Text;
			Token token = Advance();
			if (token.Kind != TokenKind.Identifier && token.Kind != TokenKind.Keyword)
				throw new SyntaxError($"unexpected '{token.Text}' after '.'", token.Line);
			return token.Text;
		}

		// The callee of new: member accesses bind tighter than the call parentheses that follow
		private Expression ParseNewCallee()
		{
			Expression expression;
			if (IsKeyword("new"))
			{
				Token keyword = Advance();
				NewExpression inner = new NewExpression { Callee = ParseNewCallee(), Line = keyword.Line };
				if (IsPunctuator("("))
					ParseArguments(inner.Arguments);
				expression = inner;
			}
			else
				expression = ParsePrimary();
			while (true)
			{
				if (Match("."))
					expression = new MemberExpression { Object = expression, Name = ParseMemberName(), Line = Current.Line };
				else if (Match("["))
				{
					expression = new MemberExpression { Object = expression, Property = ParseExpression(), Computed = true, Line = Current.Line };
					Expect("]");
				}
				else
					return expression;
			}
		}

		private void ParseArguments(List<Expression> arguments)
		{
			Expect("(");
			while (!IsPunctuator(")"))
			{
				if (Match("..."))
					arguments.Add(new SpreadElement { Argument = ParseAssignment(true), Line = Current.Line });
				else
					arguments.Add(ParseAssignment(true));
				if (!IsPunctuator(")"))
					Expect(",");
			}
			Expect(")");
		}

		private Expression ParsePrimary()
		{
			Token token = Current;
			switch (token.Kind)
			{
				case TokenKind.Number:
					Advance();
					return new Literal { Value = token.Number, Line = token.Line };
				case TokenKind.String:
					Advance();
					return new Literal { Value = token.Value, Line = token.Line };
				case TokenKind.Template:
					Advance();
					return ParseTemplate(token);
				case TokenKind.RegExp:
					Advance();
					return new RegExpLiteral { Pattern = token.Value, Flags = token.Text, Line = token.Line };
				case TokenKind.Identifier:
					Advance();
					if (token.Text == "arguments")
						MarkArguments();
					return new Identifier { Name = token.Text, Line = token.Line };
				case TokenKind.Keyword:
					switch (token.Text)
					{
						case "this":
							Advance();
							return new ThisExpression { Line = token.Line };
						case "super":
							Advance();
							return new SuperExpression { Line = token.Line };
						case "null":
							Advance();
							return new Literal { Value = JsValue.Null, Line = token.Line };
						case "undefined":
							Advance();
							return new Identifier { Name = "undefined", Line = token.Line };
						case "true":
							Advance();
							return new Literal { Value = JsValue.True, Line = token.Line };
						case "false":
							Advance();
							return new Literal { Value = JsValue.False, Line = token.Line };
						case "function":
							return new FunctionExpression { Function = ParseFunction(false, true, true), Line = token.Line };
						case "async":
							if (Peek(1).Kind == TokenKind.Keyword && Peek(1).Text == "function" && !Peek(1).NewlineBefore)
							{
								Advance();
								return new FunctionExpression { Function = ParseFunction(true, true, true), Line = token.Line };
							}
							Advance();
							return new Identifier { Name = token.Text, Line = token.Line };
						case "class":
							return ParseClass(false);
						case "new":
							return ParseLeftHandSide();
						default:
							if (IsContextualKeyword(token.Text))
							{
								Advance();
								return new Identifier { Name = token.Text, Line = token.Line };
							}
							throw Error($"unexpected '{token.Text}'");
					}
				case TokenKind.Punctuator:
					switch (token.Text)
					{
						case "(":
						{
							Advance();
							Expression inner = ParseExpression();
							Expect(")");
							return inner;
						}
						case "[":
							return ParseArrayLiteral();
						case "{":
							return ParseObjectLiteral();
						case "#":
							Advance();
							return new Identifier { Name = "#" + Advance().Text, Line = token.Line };
					}
					throw Error($"unexpected '{token.Text}'");
				default:
					throw Error("unexpected end of input");
			}
		}

		// The nearest non-arrow function sees arguments; arrows inherit the object from it
		private void MarkArguments()
		{
			for (int i = _functions.Count - 1; i >= 0; i--)
			{
				if (!_functions[i].IsArrow)
				{
					_functions[i].UsesArguments = true;
					return;
				}
			}
		}

		private TemplateLiteral ParseTemplate(Token token)
		{
			TemplateLiteral template = new TemplateLiteral { Line = token.Line };
			template.Strings.AddRange(token.TemplateStrings);
			foreach (string source in token.TemplateExpressions)
			{
				Parser inner = new Parser(source) { _inAsync = _inAsync, _inGenerator = _inGenerator, _functionDepth = _functionDepth };
				inner._functions.AddRange(_functions);
				template.Expressions.Add(inner.ParseExpression());
			}
			return template;
		}

		private ArrayLiteral ParseArrayLiteral()
		{
			ArrayLiteral array = new ArrayLiteral { Line = Current.Line };
			Expect("[");
			while (!IsPunctuator("]"))
			{
				if (Match(","))
				{
					array.Elements.Add(null);
					continue;
				}
				if (Match("..."))
					array.Elements.Add(new SpreadElement { Argument = ParseAssignment(true), Line = Current.Line });
				else
					array.Elements.Add(ParseAssignment(true));
				if (!IsPunctuator("]"))
					Expect(",");
			}
			Expect("]");
			return array;
		}

		private ObjectLiteral ParseObjectLiteral()
		{
			ObjectLiteral literal = new ObjectLiteral { Line = Current.Line };
			Expect("{");
			while (!IsPunctuator("}"))
			{
				ObjectProperty property = new ObjectProperty();
				if (Match("..."))
				{
					property.IsSpread = true;
					property.Value = ParseAssignment(true);
					literal.Properties.Add(property);
					if (!IsPunctuator("}"))
						Expect(",");
					continue;
				}
				bool isAsync = false;
				bool isGenerator = false;
				if (IsKeyword("async") && !(Peek(1).Kind == TokenKind.Punctuator && (Peek(1).Text == "(" || Peek(1).Text == ":" || Peek(1).Text == "," || Peek(1).Text == "}")) && !Peek(1).NewlineBefore)
				{
					Advance();
					isAsync = true;
				}
				if (Match("*"))
					isGenerator = true;
				if ((IsKeyword("get") || IsKeyword("set")) && !(Peek(1).Kind == TokenKind.Punctuator && (Peek(1).Text == "(" || Peek(1).Text == ":" || Peek(1).Text == "," || Peek(1).Text == "}")))
				{
					string accessor = Advance().Text;
					ParsePropertyName(out property.Key, out property.ComputedKey);
					FunctionNode function = new FunctionNode { IsMethod = true, Name = property.Key, Line = Current.Line };
					ParseParameters(function);
					function.Body = ParseFunctionBody(function);
					if (accessor == "get")
						property.Getter = function;
					else
						property.Setter = function;
					literal.Properties.Add(property);
					if (!IsPunctuator("}"))
						Expect(",");
					continue;
				}
				Token keyToken = Current;
				ParsePropertyName(out property.Key, out property.ComputedKey);
				if (IsPunctuator("("))
				{
					FunctionNode function = new FunctionNode { IsMethod = true, IsAsync = isAsync, IsGenerator = isGenerator, Name = property.Key, Line = Current.Line };
					ParseParameters(function);
					function.Body = ParseFunctionBody(function);
					property.Value = new FunctionExpression { Function = function, Line = function.Line };
					property.IsShorthandMethod = true;
				}
				else if (Match(":"))
					property.Value = ParseAssignment(true);
				else if (keyToken.Kind == TokenKind.Identifier || keyToken.Kind == TokenKind.Keyword)
				{
					// Shorthand { name }, possibly with a default when this object is really a pattern
					property.Value = new Identifier { Name = property.Key, Line = keyToken.Line };
					if (Match("="))
						property.Value = new AssignmentExpression { Operator = "=", Target = property.Value, Value = ParseAssignment(true), Line = keyToken.Line };
				}
				else
					throw Error("expected ':' in object literal");
				literal.Properties.Add(property);
				if (!IsPunctuator("}"))
					Expect(",");
			}
			Expect("}");
			return literal;
		}
	}
}
