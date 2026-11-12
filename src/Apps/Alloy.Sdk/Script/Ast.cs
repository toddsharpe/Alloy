using System.Collections.Generic;

namespace Alloy.Sdk.Script
{
	// The syntax tree: statements and expressions as the interpreter walks them
	public abstract class Node
	{
		public int Line;
	}

	public abstract class Statement : Node
	{
	}

	public abstract class Expression : Node
	{
	}

	public sealed class Program : Node
	{
		public readonly List<Statement> Body = new List<Statement>();
		public bool IsModule;
	}

	// import x, { a as b } from "m"; Imported is "default" for the default binding and "*" for a namespace
	public sealed class ImportSpecifier
	{
		public string Imported;
		public string Local;
	}

	public sealed class ImportDeclaration : Statement
	{
		public string Source;
		public readonly List<ImportSpecifier> Specifiers = new List<ImportSpecifier>();
	}

	// export { a as b }; Local is "*" for export *, with Exported null when the names are merged
	public sealed class ExportSpecifier
	{
		public string Local;
		public string Exported;
	}

	public sealed class ExportDeclaration : Statement
	{
		public Statement Declaration;
		public Expression Default;
		public string Source;
		public readonly List<ExportSpecifier> Specifiers = new List<ExportSpecifier>();
	}

	public sealed class ImportCall : Expression
	{
		public Expression Specifier;
	}

	public sealed class ImportMeta : Expression
	{
	}

	public sealed class BlockStatement : Statement
	{
		public readonly List<Statement> Body = new List<Statement>();
	}

	public sealed class EmptyStatement : Statement
	{
	}

	public sealed class ExpressionStatement : Statement
	{
		public Expression Expression;
	}

	public enum DeclarationKind
	{
		Var,
		Let,
		Const,
	}

	public sealed class VariableDeclarator
	{
		public Node Target;
		public Expression Init;
	}

	public sealed class VariableDeclaration : Statement
	{
		public DeclarationKind Kind;
		public readonly List<VariableDeclarator> Declarations = new List<VariableDeclarator>();
	}

	public sealed class FunctionNode : Node
	{
		public string Name;
		public readonly List<Node> Parameters = new List<Node>();
		public readonly List<Expression> Defaults = new List<Expression>();
		public Node RestParameter;
		public BlockStatement Body;
		public Expression ExpressionBody;
		public bool IsArrow;
		public bool IsAsync;
		public bool IsGenerator;
		public bool IsMethod;
		public bool Strict;
		public bool UsesArguments;
		public bool UsesThis;
	}

	public sealed class FunctionDeclaration : Statement
	{
		public FunctionNode Function;
	}

	public sealed class ReturnStatement : Statement
	{
		public Expression Argument;
	}

	public sealed class IfStatement : Statement
	{
		public Expression Test;
		public Statement Consequent;
		public Statement Alternate;
	}

	public sealed class ForStatement : Statement
	{
		public Node Init;
		public Expression Test;
		public Expression Update;
		public Statement Body;
		public string Label;
	}

	public sealed class ForInStatement : Statement
	{
		public Node Left;
		public Expression Right;
		public Statement Body;
		public bool IsOf;
		public string Label;
	}

	public sealed class WhileStatement : Statement
	{
		public Expression Test;
		public Statement Body;
		public bool IsDoWhile;
		public string Label;
	}

	public sealed class BreakStatement : Statement
	{
		public string Label;
	}

	public sealed class ContinueStatement : Statement
	{
		public string Label;
	}

	public sealed class ThrowStatement : Statement
	{
		public Expression Argument;
	}

	public sealed class TryStatement : Statement
	{
		public BlockStatement Block;
		public Node CatchParameter;
		public BlockStatement Handler;
		public BlockStatement Finalizer;
	}

	public sealed class SwitchCase
	{
		public Expression Test;
		public readonly List<Statement> Body = new List<Statement>();
	}

	public sealed class SwitchStatement : Statement
	{
		public Expression Discriminant;
		public readonly List<SwitchCase> Cases = new List<SwitchCase>();
		public string Label;
	}

	public sealed class LabeledStatement : Statement
	{
		public string Label;
		public Statement Body;
	}

	public sealed class ClassMember
	{
		public string Name;
		public Expression ComputedName;
		public FunctionNode Function;
		public Expression Value;
		public bool IsStatic;
		public bool IsGetter;
		public bool IsSetter;
		public bool IsField;
	}

	public sealed class ClassNode : Expression
	{
		public string Name;
		public Expression SuperClass;
		public FunctionNode Constructor;
		public readonly List<ClassMember> Members = new List<ClassMember>();
	}

	public sealed class ClassDeclaration : Statement
	{
		public ClassNode Class;
	}

	public sealed class Identifier : Expression
	{
		public string Name;
	}

	public sealed class Literal : Expression
	{
		public JsValue Value;
	}

	public sealed class RegExpLiteral : Expression
	{
		public string Pattern;
		public string Flags;
	}

	public sealed class TemplateLiteral : Expression
	{
		public readonly List<string> Strings = new List<string>();
		public readonly List<Expression> Expressions = new List<Expression>();
		public Expression Tag;
	}

	public sealed class ArrayLiteral : Expression
	{
		// Null entries are holes; spread elements are SpreadElement nodes
		public readonly List<Expression> Elements = new List<Expression>();
	}

	public sealed class ObjectProperty
	{
		public string Key;
		public Expression ComputedKey;
		public Expression Value;
		public FunctionNode Getter;
		public FunctionNode Setter;
		public bool IsSpread;
		public bool IsShorthandMethod;
	}

	public sealed class ObjectLiteral : Expression
	{
		public readonly List<ObjectProperty> Properties = new List<ObjectProperty>();
	}

	public sealed class FunctionExpression : Expression
	{
		public FunctionNode Function;
	}

	public sealed class UnaryExpression : Expression
	{
		public string Operator;
		public Expression Argument;
	}

	public sealed class UpdateExpression : Expression
	{
		public string Operator;
		public bool Prefix;
		public Expression Argument;
	}

	public sealed class BinaryExpression : Expression
	{
		public string Operator;
		public Expression Left;
		public Expression Right;
	}

	public sealed class LogicalExpression : Expression
	{
		public string Operator;
		public Expression Left;
		public Expression Right;
	}

	public sealed class AssignmentExpression : Expression
	{
		public string Operator;
		public Node Target;
		public Expression Value;
	}

	public sealed class ConditionalExpression : Expression
	{
		public Expression Test;
		public Expression Consequent;
		public Expression Alternate;
	}

	public sealed class SequenceExpression : Expression
	{
		public readonly List<Expression> Expressions = new List<Expression>();
	}

	public sealed class MemberExpression : Expression
	{
		public Expression Object;
		public Expression Property;
		public string Name;
		public bool Computed;
		public bool Optional;
	}

	public sealed class CallExpression : Expression
	{
		public Expression Callee;
		public readonly List<Expression> Arguments = new List<Expression>();
		public bool Optional;
	}

	public sealed class NewExpression : Expression
	{
		public Expression Callee;
		public readonly List<Expression> Arguments = new List<Expression>();
	}

	public sealed class SpreadElement : Expression
	{
		public Expression Argument;
	}

	public sealed class ThisExpression : Expression
	{
	}

	public sealed class SuperExpression : Expression
	{
	}

	public sealed class NewTargetExpression : Expression
	{
	}

	public sealed class AwaitExpression : Expression
	{
		public Expression Argument;
	}

	public sealed class YieldExpression : Expression
	{
		public Expression Argument;
		public bool Delegate;
	}

	// Destructuring patterns, in declarations, parameters and assignments
	public sealed class ArrayPattern : Node
	{
		public readonly List<Node> Elements = new List<Node>();
		public readonly List<Expression> Defaults = new List<Expression>();
		public Node Rest;
	}

	public sealed class PatternProperty
	{
		public string Key;
		public Expression ComputedKey;
		public Node Target;
		public Expression Default;
	}

	public sealed class ObjectPattern : Node
	{
		public readonly List<PatternProperty> Properties = new List<PatternProperty>();
		public Node Rest;
	}
}
