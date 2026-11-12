using System;
using System.Collections.Generic;

namespace Alloy.Sdk.Script
{
	// A module: its source, its own scope, and its exports as the bindings themselves, so imports stay live
	public sealed class ModuleRecord
	{
		public string Url;
		public Program Program;
		public Scope Scope;
		public readonly Dictionary<string, Binding> Exports = new Dictionary<string, Binding>(StringComparer.Ordinal);
		public readonly List<ModuleRecord> StarExports = new List<ModuleRecord>();
		public JsObject Namespace;
		public ModuleState State;
		public JsValue Error;
	}

	public enum ModuleState
	{
		Unlinked,
		Linking,
		Linked,
		Evaluating,
		Evaluated,
		Failed,
	}

	// An absolute module URL for a specifier seen from a referrer, or null when it cannot be resolved
	public delegate string ModuleResolver(string specifier, string referrer);

	// A module's source text by URL, or null when it cannot be fetched
	public delegate string ModuleFetcher(string url);

	// The namespace object of a module: every export readable, nothing writable
	public sealed class JsModuleNamespace : JsObject
	{
		private readonly Interpreter _interpreter;
		public readonly ModuleRecord Module;

		public JsModuleNamespace(Interpreter interpreter, ModuleRecord module) : base(null)
		{
			_interpreter = interpreter;
			Module = module;
			Class = "Module";
			Extensible = false;
		}

		public override JsProperty GetOwnProperty(string key)
		{
			if (key == Interpreter.SymbolToStringTag)
				return new JsProperty { Value = "Module", Enumerable = false, Writable = false };
			Binding binding = _interpreter.ResolveExport(Module, key, null);
			return binding == null ? null : new JsProperty { Value = binding.Initialized ? binding.Value : JsValue.Undefined, Writable = false };
		}

		public override JsValue Get(string key, Interpreter interpreter, JsValue receiver)
		{
			if (key == Interpreter.SymbolToStringTag)
				return "Module";
			Binding binding = _interpreter.ResolveExport(Module, key, null);
			if (binding == null)
				return JsValue.Undefined;
			if (!binding.Initialized)
				throw interpreter.ReferenceError("Cannot access '" + key + "' before initialization");
			return binding.Value;
		}

		public override void Set(string key, JsValue value, Interpreter interpreter, JsValue receiver)
		{
		}

		public override bool Delete(string key) => false;
	}

	// ES modules: linked by sharing bindings, evaluated depth first, fetched through the host
	public sealed partial class Interpreter
	{
		public ModuleResolver ResolveModule;
		public ModuleFetcher FetchModule;
		private readonly Dictionary<string, ModuleRecord> _modules = new Dictionary<string, ModuleRecord>(StringComparer.Ordinal);

		// Parses, links and runs a module from source, as a script tag of type module does
		public JsValue EvaluateModule(string source, string url)
		{
			Interpreter prior = Current;
			Current = this;
			try
			{
				ModuleRecord record = Register(url, source);
				Link(record);
				Evaluate(record);
				RunJobs();
				return JsValue.Undefined;
			}
			finally
			{
				Current = prior;
			}
		}

		public bool TryEvaluateModule(string source, string url)
		{
			try
			{
				EvaluateModule(source, url);
				return true;
			}
			catch (JsThrow t)
			{
				ReportError(t.Value);
				return false;
			}
		}

		private ModuleRecord Register(string url, string source)
		{
			if (_modules.TryGetValue(url, out ModuleRecord existing))
				return existing;
			Program program;
			try
			{
				program = Parser.ParseModule(source);
			}
			catch (SyntaxError e)
			{
				throw new JsThrow(NewError("SyntaxError", e.Message + " in " + url));
			}
			ModuleRecord record = new ModuleRecord { Url = url, Program = program };
			record.Scope = new Scope(GlobalScope, true) { HasThis = true, ThisValue = JsValue.Undefined, Module = record };
			_modules[url] = record;
			return record;
		}

		// The module a specifier names from a referrer, fetched and registered if this is its first mention
		private ModuleRecord Load(string specifier, string referrer)
		{
			string url = ResolveModule?.Invoke(specifier, referrer) ?? DefaultResolve(specifier, referrer);
			if (url == null)
				throw TypeError("Failed to resolve module specifier \"" + specifier + "\"" + (referrer == null ? "" : " from " + referrer));
			if (_modules.TryGetValue(url, out ModuleRecord existing))
				return existing;
			string source = FetchModule?.Invoke(url);
			if (source == null)
				throw TypeError("Failed to fetch dynamically imported module: " + url);
			return Register(url, source);
		}

		public static string DefaultResolve(string specifier, string referrer)
		{
			if (Uri.TryCreate(specifier, UriKind.Absolute, out Uri absolute) && specifier.Contains("://"))
				return absolute.ToString();
			if (!(specifier.StartsWith("./", StringComparison.Ordinal) || specifier.StartsWith("../", StringComparison.Ordinal) || specifier.StartsWith("/", StringComparison.Ordinal)))
				return null;
			if (referrer != null && Uri.TryCreate(referrer, UriKind.Absolute, out Uri baseUri) && Uri.TryCreate(baseUri, specifier, out Uri resolved))
				return resolved.ToString();
			return null;
		}

		// Hoists the module's declarations, binds its imports to the exporting modules' bindings, and records its own exports
		private void Link(ModuleRecord record)
		{
			if (record.State != ModuleState.Unlinked)
				return;
			record.State = ModuleState.Linking;
			Scope scope = record.Scope;
			List<Statement> body = record.Program.Body;
			HoistVars(body, scope, false);
			HoistLexical(body, scope, false);
			foreach (Statement statement in body)
			{
				if (statement is not ExportDeclaration export)
					continue;
				if (export.Declaration != null)
				{
					foreach (string name in DeclaredNames(export.Declaration))
						record.Exports[name] = scope.Bindings[name];
				}
				else if (export.Default != null)
					record.Exports["default"] = scope.Bindings["*default*"];
			}
			foreach (Statement statement in body)
			{
				if (statement is not ImportDeclaration import)
					continue;
				ModuleRecord dependency = Load(import.Source, record.Url);
				Link(dependency);
				foreach (ImportSpecifier specifier in import.Specifiers)
				{
					if (specifier.Imported == "*")
					{
						scope.Declare(specifier.Local, NamespaceOf(dependency), false);
						continue;
					}
					Binding binding = ResolveExport(dependency, specifier.Imported, null);
					if (binding == null)
						throw SyntaxErrorThrow("The requested module '" + import.Source + "' does not provide an export named '" + specifier.Imported + "'");
					scope.Bindings[specifier.Local] = binding;
				}
			}
			foreach (Statement statement in body)
			{
				if (statement is not ExportDeclaration export || export.Default != null || export.Specifiers.Count == 0)
					continue;
				ModuleRecord source = export.Source == null ? null : Load(export.Source, record.Url);
				if (source != null)
					Link(source);
				foreach (ExportSpecifier specifier in export.Specifiers)
				{
					if (specifier.Local == "*")
					{
						if (specifier.Exported == null)
							record.StarExports.Add(source);
						else
							record.Exports[specifier.Exported] = new Binding { Value = NamespaceOf(source), Initialized = true };
						continue;
					}
					Binding binding = source == null ? scope.Lookup(specifier.Local) : ResolveExport(source, specifier.Local, null);
					if (binding == null)
						throw SyntaxErrorThrow(source == null ? "Export '" + specifier.Local + "' is not defined in module" : "The requested module '" + export.Source + "' does not provide an export named '" + specifier.Local + "'");
					record.Exports[specifier.Exported] = binding;
				}
			}
			record.State = ModuleState.Linked;
		}

		private static IEnumerable<string> DeclaredNames(Statement declaration)
		{
			switch (declaration)
			{
				case VariableDeclaration variables:
					foreach (VariableDeclarator declarator in variables.Declarations)
					{
						foreach (string name in PatternNames(declarator.Target))
							yield return name;
					}
					break;
				case FunctionDeclaration function:
					yield return function.Function.Name;
					break;
				case ClassDeclaration cls:
					yield return cls.Class.Name;
					break;
			}
		}

		// An export by name, through export * chains; default never comes through a star
		public Binding ResolveExport(ModuleRecord record, string name, HashSet<ModuleRecord> visited)
		{
			if (record.Exports.TryGetValue(name, out Binding binding))
				return binding;
			if (name == "default" || record.StarExports.Count == 0)
				return null;
			visited ??= new HashSet<ModuleRecord>();
			if (!visited.Add(record))
				return null;
			foreach (ModuleRecord star in record.StarExports)
			{
				Binding found = ResolveExport(star, name, visited);
				if (found != null)
					return found;
			}
			return null;
		}

		public JsObject NamespaceOf(ModuleRecord record) => record.Namespace ??= new JsModuleNamespace(this, record);

		// Runs dependencies first, then the module body; a module in a cycle is left to its first evaluation
		private void Evaluate(ModuleRecord record)
		{
			if (record.State == ModuleState.Failed)
				throw new JsThrow(record.Error);
			if (record.State != ModuleState.Linked)
				return;
			record.State = ModuleState.Evaluating;
			try
			{
				foreach (Statement statement in record.Program.Body)
				{
					string source = statement is ImportDeclaration import ? import.Source : statement is ExportDeclaration { Source: not null } export ? export.Source : null;
					if (source != null)
						Evaluate(Load(source, record.Url));
				}
				_running++;
				try
				{
					foreach (Statement statement in record.Program.Body)
						Execute(statement, record.Scope);
				}
				finally
				{
					_running--;
				}
				record.State = ModuleState.Evaluated;
			}
			catch (JsThrow t)
			{
				record.State = ModuleState.Failed;
				record.Error = t.Value;
				throw;
			}
		}

		private static ModuleRecord ModuleOf(Scope scope)
		{
			for (Scope s = scope; s != null; s = s.Parent)
			{
				if (s.Module is ModuleRecord record)
					return record;
			}
			return null;
		}

		// import(specifier): the namespace on a promise, loaded on a job
		private JsValue DynamicImport(ImportCall call, Scope scope)
		{
			string specifier = Evaluate(call.Specifier, scope).ToJsString();
			string referrer = ModuleOf(scope)?.Url ?? ScriptUrl;
			JsPromise promise = NewPromise();
			EnqueueJob(() =>
			{
				try
				{
					ModuleRecord record = Load(specifier, referrer);
					Link(record);
					Evaluate(record);
					ResolvePromise(promise, NamespaceOf(record));
				}
				catch (JsThrow t)
				{
					RejectPromise(promise, t.Value);
				}
			});
			return promise;
		}

		// The URL classic scripts import relative to, set by the host
		public string ScriptUrl;

		private JsValue ImportMetaFor(Scope scope)
		{
			ModuleRecord record = ModuleOf(scope);
			JsObject meta = new JsObject(null);
			meta.Put("url", record?.Url ?? ScriptUrl ?? "");
			DefineMethod(meta, "resolve", 1, (i, t, a) => ResolveModule?.Invoke(Arg(a, 0).ToJsString(), record?.Url) ?? DefaultResolve(Arg(a, 0).ToJsString(), record?.Url) ?? Arg(a, 0).ToJsString());
			return meta;
		}
	}
}
