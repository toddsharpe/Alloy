using System;
using System.Collections.Generic;
using System.Text;
using Alloy.Sdk.Script;
using Alloy.Sdk.Web.Html;
using Node = Alloy.Sdk.Web.Html.Node;
using Alloy.Sdk.Web.Layout;

namespace Alloy.Sdk.Web.Dom
{
	// What a fetch from a script got back
	public sealed class ResourceResponse
	{
		public int Status;
		public string Reason = "";
		public Uri Url;
		public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		public byte[] Body = Array.Empty<byte>();
		public string Error;

		public bool Ok => Status >= 200 && Status < 300;
	}

	public delegate void FocusHandler(Element previous, Element current);

	// Fetches a resource for scripts; headers are the ones the script set on the request, Accept among them, or null
	public delegate ResourceResponse ResourceFetcher(string method, Uri url, byte[] body, string contentType, IReadOnlyList<KeyValuePair<string, string>> headers);

	public delegate LayoutBox BoxLookup(Element element);

	public delegate Alloy.Sdk.Web.Css.ComputedStyle StyleLookup(Element element);

	public delegate void NavigationHandler(FormSubmission request);

	public delegate void HistoryHandler(int delta);

	public delegate void TextHandler(string text);

	public delegate void ElementHandler(Element element);

	public delegate void ScrollHandler(int y);

	public delegate int ScrollReader();

	public delegate void LayoutRequest();

	public delegate void ConsoleHandler(string level, string text);

	// A listener registered with addEventListener, or a property handler such as onclick
	internal sealed class Listener
	{
		public string Type;
		public JsValue Callback;
		public bool Capture;
		public bool Once;
	}

	// The script side of a page: the interpreter, the window and document it sees, the scripts it ran and the events it receives
	public sealed partial class ScriptHost
	{
		public readonly Document Document;
		public readonly Interpreter Interpreter;
		public Uri Url;
		public ResourceFetcher Fetch;
		public BoxLookup BoxFor;
		// The cascade's style for an element without a layout, for computed values that need no geometry
		public StyleLookup StyleFor;
		public LayoutRequest EnsureLayout;
		public NavigationHandler Navigate;
		public HistoryHandler HistoryMove;
		public TextHandler Alert;
		public ElementHandler Focus;
		public ScrollHandler ScrollTo;
		public ScrollReader ScrollY;
		public ConsoleHandler Console;
		public int ViewportWidth = 1200;
		public int ViewportHeight = 746;
		public double ScriptBudgetMs = 8000;
		public double EventBudgetMs = 3000;
		// Wall time all the scripts of a page load may take together, fetches included; what does not fit is skipped so the page still appears
		public double LoadBudgetMs = 20000;
		// How long one pump may spend on due timers before handing control back, so the window keeps painting and taking input
		public double PumpSliceMs = 100;
		private double _loadDeadline = double.PositiveInfinity;
		public string ReadyState = "loading";
		// The document changed since it was last laid out
		public bool Dirty;
		public bool TitleChanged;
		public int ScriptsRun;
		public int ScriptErrors;
		public int ScriptsSkipped;

		private readonly Dictionary<Node, JsNode> _wrappers = new Dictionary<Node, JsNode>();
		private readonly Dictionary<object, List<Listener>> _listeners = new Dictionary<object, List<Listener>>();
		private readonly Dictionary<object, Dictionary<string, JsValue>> _handlers = new Dictionary<object, Dictionary<string, JsValue>>();
		private readonly HashSet<Element> _ranScripts = new HashSet<Element>();
		private Element _currentScript;
		private Element _activeElement;
		private readonly List<string> _logTail = new List<string>();

		public ScriptHost(Document document, Uri url)
		{
			Document = document;
			Url = url;
			Interpreter = new Interpreter();
			Interpreter.OnConsole = (level, text) => Log(level, text);
			Interpreter.OnError = text =>
			{
				ScriptErrors++;
				Log("error", text);
			};
			SetupPrototypes();
			SetupWindow();
			SetupDocument();
		}

		// The last console lines, for a status display
		public IReadOnlyList<string> LogTail => _logTail;

		private void Log(string level, string text)
		{
			if (_logTail.Count >= 50)
				_logTail.RemoveAt(0);
			_logTail.Add(text);
			Console?.Invoke(level, text);
		}

		public double Now => Interpreter.Clock();

		// Scripts

		// Runs the classic scripts in document order, then the modules (which defer), then fires DOMContentLoaded
		public void RunScripts()
		{
			_loadDeadline = Now + LoadBudgetMs;
			Interpreter.ScriptUrl = Url?.ToString();
			Interpreter.ResolveModule = ResolveSpecifier;
			Interpreter.FetchModule = FetchModuleSource;
			foreach (Element script in new List<Element>(Document.DescendantElements()))
			{
				if (script.TagName == "script" && ScriptKind(script) == "importmap")
					LoadImportMap(script);
			}
			// Classic scripts first, then modules, which the parser defers; an await that cannot progress runs the next one early
			_queue.Clear();
			_queueAt = 0;
			foreach (Element script in new List<Element>(Document.DescendantElements()))
			{
				if (script.TagName == "script" && ScriptKind(script) == "classic")
					_queue.Add(script);
			}
			foreach (Element script in new List<Element>(Document.DescendantElements()))
			{
				if (script.TagName == "script" && ScriptKind(script) == "module")
					_queue.Add(script);
			}
			Interpreter.HostPump = PumpForAwait;
			while (_queueAt < _queue.Count)
				RunScript(_queue[_queueAt++]);
			ReadyState = "interactive";
			DispatchSimple("readystatechange", Document, false, false);
			DispatchSimple("DOMContentLoaded", Document, true, false);
			RunJobsBudgeted();
		}

		private readonly List<Element> _queue = new List<Element>();
		private int _queueAt;

		// What an await does while its promise is pending: a browser would run the scripts still to come and fire timers before resuming, so this does the same in that order
		private bool PumpForAwait()
		{
			if (_queueAt < _queue.Count)
			{
				RunScript(_queue[_queueAt++]);
				return true;
			}
			if (!Interpreter.HasTimers)
				return false;
			double wait = Interpreter.NextTimerDue - Now;
			if (wait > 0)
				System.Threading.Thread.Sleep((int)Math.Min(wait, 250));
			Interpreter.RunTimers();
			return true;
		}

		// Pending promise jobs under a budget, never unbounded: a page that chains work forever must not hold the load
		private void RunJobsBudgeted()
		{
			Interpreter.Deadline = EventDeadline();
			try
			{
				Interpreter.RunJobs();
			}
			finally
			{
				Interpreter.Deadline = double.PositiveInfinity;
			}
		}

		// During a page load, the handlers and jobs an event sets off share the page budget; afterwards each event gets its own
		private double EventDeadline() => double.IsPositiveInfinity(_loadDeadline) ? Now + EventBudgetMs : _loadDeadline;

		// The load event, once the page's resources are in
		public void FireLoad()
		{
			ReadyState = "complete";
			DispatchSimple("readystatechange", Document, false, false);
			JsEvent load = NewEvent("load", false, false);
			DispatchEvent(load, Interpreter.Global);
			DispatchSimple("pageshow", Interpreter.Global, false, false);
			RunJobsBudgeted();
			_loadDeadline = double.PositiveInfinity;
		}

		// classic, module, importmap, or other for the data blocks pages keep in script tags; nomodule scripts are for browsers without modules
		private static string ScriptKind(Element script)
		{
			string type = (script.GetAttribute("type") ?? "").Trim().ToLowerInvariant();
			if (type == "module")
				return "module";
			if (type == "importmap")
				return "importmap";
			if (type.Length == 0 || type == "text/javascript" || type == "application/javascript" || type == "text/ecmascript" || type == "application/ecmascript" || type == "text/jscript")
				return script.HasAttribute("nomodule") ? "other" : "classic";
			return "other";
		}

		private readonly Dictionary<string, string> _importMap = new Dictionary<string, string>(StringComparer.Ordinal);
		private int _inlineModules;

		private void LoadImportMap(Element script)
		{
			if (!_ranScripts.Add(script))
				return;
			try
			{
				JsValue parse = Interpreter.Global.Get("JSON", Interpreter).Object.Get("parse", Interpreter);
				JsValue map = Interpreter.Call(parse, JsValue.Undefined, new JsValue[] { script.TextContent });
				JsValue imports = Interpreter.GetMember(map, "imports");
				if (!imports.IsObject)
					return;
				foreach (string key in Interpreter.OwnEnumerableKeys(imports.Object))
				{
					string target = imports.Object.Get(key, Interpreter).ToJsString();
					_importMap[key] = Resolve(target)?.ToString() ?? target;
				}
			}
			catch (JsThrow t)
			{
				Interpreter.ReportError(t.Value);
			}
		}

		// Bare specifiers go through the import map, including its prefix entries; the rest resolve as URLs
		private string ResolveSpecifier(string specifier, string referrer)
		{
			if (_importMap.TryGetValue(specifier, out string mapped))
				return mapped;
			foreach (KeyValuePair<string, string> entry in _importMap)
			{
				if (entry.Key.EndsWith("/", StringComparison.Ordinal) && specifier.StartsWith(entry.Key, StringComparison.Ordinal))
					return entry.Value + specifier.Substring(entry.Key.Length);
			}
			return Interpreter.DefaultResolve(specifier, referrer ?? Url?.ToString());
		}

		private string FetchModuleSource(string url)
		{
			if (Fetch == null || !Uri.TryCreate(url, UriKind.Absolute, out Uri target))
				return null;
			ResourceResponse response = Fetch("GET", target, null, null, null);
			if (response == null || !response.Ok)
			{
				Log("error", $"module: {url} failed ({response?.Error ?? response?.Status.ToString() ?? "no response"})");
				return null;
			}
			return Decode(response.Body);
		}

		public void RunScript(Element script)
		{
			if (!_ranScripts.Add(script))
				return;
			NewSlice();
			string kind = ScriptKind(script);
			if (kind == "importmap")
			{
				_ranScripts.Remove(script);
				LoadImportMap(script);
				return;
			}
			if (kind == "other")
				return;
			string source;
			string src = script.GetAttribute("src");
			Uri target = null;
			if (Now > _loadDeadline)
			{
				ScriptsSkipped++;
				if (ScriptsSkipped == 1)
					Log("warn", "script: page script budget spent, later scripts skipped");
				return;
			}
			if (!string.IsNullOrWhiteSpace(src))
			{
				target = Resolve(src.Trim());
				if (target == null || Fetch == null)
					return;
				ResourceResponse response = Fetch("GET", target, null, null, null);
				if (response == null || !response.Ok)
				{
					Log("error", $"script: {target} failed ({response?.Error ?? response?.Status.ToString() ?? "no response"})");
					DispatchSimple("error", script, false, false);
					return;
				}
				source = Decode(response.Body);
			}
			else
				source = script.TextContent;
			Element previous = _currentScript;
			_currentScript = script;
			// A script run from inside another's await gives that script its deadline back, moved on by the time this one took
			double savedDeadline = Interpreter.Deadline;
			double started = Now;
			Interpreter.Deadline = Math.Min(Now + ScriptBudgetMs, _loadDeadline);
			ScriptsRun++;
			try
			{
				if (kind == "module")
					Interpreter.TryEvaluateModule(source, target?.ToString() ?? (Url?.ToString() ?? "about:blank") + "#module-" + ++_inlineModules);
				else
					Interpreter.TryEvaluate(source, target?.ToString());
			}
			finally
			{
				double spent = Now - started;
				Interpreter.Deadline = double.IsPositiveInfinity(savedDeadline) ? savedDeadline : Math.Min(savedDeadline + spent, _loadDeadline);
				if (!double.IsPositiveInfinity(savedDeadline))
					Interpreter.BorrowedMs += spent;
				_currentScript = previous;
			}
			if (target != null)
				DispatchSimple("load", script, false, false);
		}

		// Scripts in a subtree that just joined the document run now, as dynamically inserted scripts do
		private void RunInsertedScripts(Node node)
		{
			if (!IsConnected(node))
				return;
			if (node is Element element && element.TagName == "script")
				RunScript(element);
			foreach (Element script in new List<Element>(node.DescendantElements()))
			{
				if (script.TagName == "script")
					RunScript(script);
			}
		}

		public static string Decode(byte[] bytes)
		{
			if (bytes == null)
				return "";
			int offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
			return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
		}

		public Uri Resolve(string href)
		{
			if (href == null)
				return null;
			if (Url != null && Uri.TryCreate(Url, href, out Uri target))
				return target;
			return Uri.TryCreate(href, UriKind.Absolute, out Uri absolute) ? absolute : null;
		}

		private bool IsConnected(Node node)
		{
			Node root = node;
			while (root.Parent != null)
				root = root.Parent;
			return root == Document;
		}

		// Timers and jobs, pumped by the host between events; true when the document changed meanwhile
		public bool Pump()
		{
			NewSlice();
			bool before = Dirty;
			Dirty = false;
			Interpreter.Deadline = Now + EventBudgetMs;
			try
			{
				Interpreter.RunJobs();
				Interpreter.RunTimers(Now + PumpSliceMs);
			}
			finally
			{
				Interpreter.Deadline = double.PositiveInfinity;
			}
			bool changed = Dirty;
			Dirty |= before;
			return changed;
		}

		public bool HasTimers => Interpreter.HasTimers;

		public double NextTimerDue => Interpreter.NextTimerDue;

		// Mutations

		internal void Mutated(Node target, string kind, string attributeName = null, List<Node> added = null, List<Node> removed = null)
		{
			Dirty = true;
			_layoutValid = false;
			if (target is Element element && element.TagName == "title" || attributeName == null && target.Parent is Element { TagName: "title" })
				TitleChanged = true;
			NotifyObservers(target, kind, attributeName, added, removed);
		}

		// Events

		internal JsEvent NewEvent(string type, bool bubbles, bool cancelable, JsObject prototype = null)
		{
			JsEvent ev = new JsEvent(prototype ?? EventPrototype)
			{
				Type = type,
				Bubbles = bubbles,
				Cancelable = cancelable,
				Trusted = true,
				TimeStamp = Now,
			};
			return ev;
		}

		// A plain event on a node or the window; true unless a handler called preventDefault
		public bool DispatchSimple(string type, object target, bool bubbles, bool cancelable)
		{
			if (target is Node node && !HasAnyListeners(node) && !(target is Document))
				return true;
			return DispatchEvent(NewEvent(type, bubbles, cancelable), target);
		}

		// Whether anything at all listens along this node's ancestor chain or on the window, so quiet pages pay nothing per click
		private bool HasAnyListeners(Node node)
		{
			if (_listeners.Count == 0 && _handlers.Count == 0)
				return false;
			for (Node n = node; n != null; n = n.Parent)
			{
				if (_listeners.ContainsKey(n) || _handlers.ContainsKey(n))
					return true;
				if (n is Element element && HasInlineHandler(element))
					return true;
			}
			return _listeners.ContainsKey(Interpreter.Global) || _handlers.ContainsKey(Interpreter.Global);
		}

		private static bool HasInlineHandler(Element element)
		{
			foreach (KeyValuePair<string, string> attribute in element.Attributes)
			{
				if (attribute.Key.Length > 2 && attribute.Key[0] == 'o' && attribute.Key[1] == 'n')
					return true;
			}
			return false;
		}

		public bool DispatchMouse(string type, Element target, float x, float y, int button = 0, bool shift = false, bool ctrl = false, bool alt = false)
		{
			if (!HasAnyListeners(target))
				return true;
			JsEvent ev = NewEvent(type, true, type != "mouseenter" && type != "mouseleave");
			ev.Prototype = MouseEventPrototype;
			int scroll = ScrollY?.Invoke() ?? 0;
			ev.Put("clientX", Math.Round(x));
			ev.Put("clientY", Math.Round(y - scroll));
			ev.Put("pageX", Math.Round(x));
			ev.Put("pageY", Math.Round(y));
			ev.Put("screenX", Math.Round(x));
			ev.Put("screenY", Math.Round(y - scroll));
			ev.Put("offsetX", Math.Round(x - (BoxFor?.Invoke(target)?.X ?? 0)));
			ev.Put("offsetY", Math.Round(y - (BoxFor?.Invoke(target)?.Y ?? 0)));
			ev.Put("button", button);
			ev.Put("buttons", type == "mousedown" ? 1 : 0);
			ev.Put("detail", type == "dblclick" ? 2 : 1);
			ev.Put("shiftKey", shift);
			ev.Put("ctrlKey", ctrl);
			ev.Put("altKey", alt);
			ev.Put("metaKey", false);
			ev.Put("relatedTarget", JsValue.Null);
			return DispatchEvent(ev, target);
		}

		public bool DispatchKey(string type, Element target, string key, string code, int keyCode, bool shift, bool ctrl, bool alt)
		{
			object receiver = (object)target ?? Document;
			if (receiver is Node node && !HasAnyListeners(node))
				return true;
			JsEvent ev = NewEvent(type, true, true);
			ev.Prototype = KeyboardEventPrototype;
			ev.Put("key", key);
			ev.Put("code", code);
			ev.Put("keyCode", keyCode);
			ev.Put("which", keyCode);
			ev.Put("charCode", type == "keypress" ? keyCode : 0);
			ev.Put("shiftKey", shift);
			ev.Put("ctrlKey", ctrl);
			ev.Put("altKey", alt);
			ev.Put("metaKey", false);
			ev.Put("repeat", false);
			ev.Put("isComposing", false);
			ev.Put("location", 0);
			return DispatchEvent(ev, receiver);
		}

		public bool DispatchInput(Element target, string data)
		{
			if (!HasAnyListeners(target))
				return true;
			JsEvent ev = NewEvent("input", true, false);
			ev.Prototype = InputEventPrototype;
			ev.Put("data", data == null ? JsValue.Null : data);
			ev.Put("inputType", data == null ? "deleteContentBackward" : "insertText");
			ev.Put("isComposing", false);
			return DispatchEvent(ev, target);
		}

		// Focus moves: blur and focusout on the old element, focus and focusin on the new one
		// Told when the focused element changes, so the page can restyle :focus
		public FocusHandler FocusStyle;

		public void SetActiveElement(Element element)
		{
			if (_activeElement == element)
				return;
			Element previous = _activeElement;
			_activeElement = element;
			FocusStyle?.Invoke(previous, element);
			if (previous != null)
			{
				DispatchSimple("blur", previous, false, false);
				DispatchSimple("focusout", previous, true, false);
			}
			if (element != null)
			{
				DispatchSimple("focus", element, false, false);
				DispatchSimple("focusin", element, true, false);
			}
		}

		public Element ActiveElement => _activeElement;

		// The event path, listeners and handlers in order; true unless preventDefault was called
		public bool DispatchEvent(JsEvent ev, object target)
		{
			if (!Interpreter.InScript)
				NewSlice();
			List<object> path = new List<object>();
			if (target is Node node)
			{
				bool connected = IsConnected(node);
				for (Node n = node; n != null; n = n.Parent)
					path.Add(n);
				if (connected && ev.Type != "load" && ev.Type != "error" && ev.Type != "scroll" && ev.Type != "readystatechange")
					path.Add(Interpreter.Global);
			}
			else
				path.Add(target);
			ev.Target = target;
			ev.Dispatching = true;
			JsEvent previousEvent = _currentEvent;
			_currentEvent = ev;
			double savedDeadline = Interpreter.Deadline;
			if (double.IsPositiveInfinity(savedDeadline))
				Interpreter.Deadline = EventDeadline();
			try
			{
				ev.Phase = 1;
				for (int i = path.Count - 1; i >= 1 && !ev.Stop; i--)
					Invoke(path[i], ev, true);
				if (!ev.Stop)
				{
					ev.Phase = 2;
					Invoke(path[0], ev, true);
					if (!ev.Stop)
						Invoke(path[0], ev, false);
				}
				if (ev.Bubbles)
				{
					ev.Phase = 3;
					for (int i = 1; i < path.Count && !ev.Stop; i++)
						Invoke(path[i], ev, false);
				}
			}
			finally
			{
				ev.Phase = 0;
				ev.CurrentTarget = null;
				ev.Dispatching = false;
				_currentEvent = previousEvent;
				Interpreter.Deadline = savedDeadline;
			}
			if (!Interpreter.InScript)
			{
				// The jobs the handlers queued run under the same budget; a handler awaiting what never comes must not hold the dispatch
				if (double.IsPositiveInfinity(savedDeadline))
					Interpreter.Deadline = EventDeadline();
				try
				{
					Interpreter.RunJobs();
				}
				finally
				{
					Interpreter.Deadline = savedDeadline;
				}
			}
			return !ev.DefaultPrevented;
		}

		private void Invoke(object current, JsEvent ev, bool capture)
		{
			ev.CurrentTarget = current;
			JsValue thisValue = WrapTarget(current);
			if (!capture)
			{
				JsValue handler = HandlerFor(current, ev.Type);
				if (handler.IsCallable)
				{
					JsValue result = Call(handler, thisValue, ev);
					if (result.IsBoolean && !result.Boolean && ev.Cancelable && ev.Type != "beforeunload")
						ev.DefaultPrevented = true;
					if (ev.StopImmediate)
						return;
				}
			}
			if (!_listeners.TryGetValue(current, out List<Listener> listeners))
				return;
			foreach (Listener listener in listeners.ToArray())
			{
				if (listener.Capture != capture || listener.Type != ev.Type)
					continue;
				if (listener.Once)
					listeners.Remove(listener);
				JsValue callback = listener.Callback;
				if (callback.IsObject && !callback.IsCallable)
				{
					JsValue handleEvent = Interpreter.GetMember(callback, "handleEvent");
					if (handleEvent.IsCallable)
						Call(handleEvent, callback, ev);
				}
				else if (callback.IsCallable)
					Call(callback, thisValue, ev);
				if (ev.StopImmediate)
					return;
			}
		}

		private JsValue Call(JsValue function, JsValue thisValue, JsEvent ev)
		{
			try
			{
				return Interpreter.Call(function, thisValue, new JsValue[] { ev });
			}
			catch (JsThrow t)
			{
				Interpreter.ReportError(t.Value);
				return JsValue.Undefined;
			}
		}

		internal JsValue WrapTarget(object target)
		{
			if (target is Node node)
				return Wrap(node);
			return target is JsObject obj ? JsValue.FromObject(obj) : JsValue.Undefined;
		}

		// Listener registration, shared by nodes, the window and other event targets

		internal void AddListener(object target, string type, JsValue callback, JsValue options)
		{
			if (callback.IsNullish)
				return;
			bool capture = options.IsObject ? Interpreter.GetMember(options, "capture").Truthy : options.Truthy;
			bool once = options.IsObject && Interpreter.GetMember(options, "once").Truthy;
			if (!_listeners.TryGetValue(target, out List<Listener> listeners))
				_listeners[target] = listeners = new List<Listener>();
			foreach (Listener existing in listeners)
			{
				if (existing.Type == type && existing.Capture == capture && JsValue.StrictEquals(existing.Callback, callback))
					return;
			}
			listeners.Add(new Listener { Type = type, Callback = callback, Capture = capture, Once = once });
		}

		internal void RemoveListener(object target, string type, JsValue callback, JsValue options)
		{
			if (!_listeners.TryGetValue(target, out List<Listener> listeners))
				return;
			bool capture = options.IsObject ? Interpreter.GetMember(options, "capture").Truthy : options.Truthy;
			listeners.RemoveAll(l => l.Type == type && l.Capture == capture && JsValue.StrictEquals(l.Callback, callback));
		}

		// The onclick-style handler for a target: set from script, or compiled from an inline attribute the first time it is needed
		internal JsValue HandlerFor(object target, string type)
		{
			if (_handlers.TryGetValue(target, out Dictionary<string, JsValue> handlers) && handlers.TryGetValue(type, out JsValue handler))
				return handler;
			if (target is Element element)
			{
				string source = element.GetAttribute("on" + type);
				if (source != null)
				{
					JsValue compiled = CompileInlineHandler(source, element);
					SetHandler(target, type, compiled);
					return compiled;
				}
			}
			else if (target == Interpreter.Global && Document.Body != null)
			{
				// Handlers written on body, such as onload, belong to the window
				if (type is "load" or "unload" or "beforeunload" or "resize" or "scroll" or "error" or "hashchange" or "popstate")
				{
					string source = Document.Body.GetAttribute("on" + type);
					if (source != null)
					{
						JsValue compiled = CompileInlineHandler(source, Document.Body);
						SetHandler(target, type, compiled);
						return compiled;
					}
				}
			}
			return JsValue.Null;
		}

		internal void SetHandler(object target, string type, JsValue handler)
		{
			if (!_handlers.TryGetValue(target, out Dictionary<string, JsValue> handlers))
				_handlers[target] = handlers = new Dictionary<string, JsValue>(StringComparer.Ordinal);
			handlers[type] = handler.IsCallable ? handler : JsValue.Null;
		}

		private JsValue CompileInlineHandler(string source, Element element)
		{
			try
			{
				FunctionNode node = Parser.ParseFunctionBody("event", source);
				JsFunction function = Interpreter.MakeFunction(node, Interpreter.GlobalScope);
				function.Name = "on" + element.TagName;
				return function;
			}
			catch (SyntaxError e)
			{
				Log("error", "inline handler: " + e.Message);
				return JsValue.Null;
			}
		}

		// Element lookups scripts share with the host

		public Element ElementById(string id)
		{
			foreach (Element element in Document.DescendantElements())
			{
				if (element.Id == id)
					return element;
			}
			return null;
		}

		// Observers: MutationObserver, IntersectionObserver and ResizeObserver, all delivered on jobs

		private sealed class Observation
		{
			public JsObject Observer;
			public JsFunction Callback;
			public Node Target;
			public bool ChildList;
			public bool Attributes;
			public bool CharacterData;
			public bool Subtree;
			public List<string> AttributeFilter;
			public JsArray Pending;
			public bool Scheduled;
		}

		private readonly List<Observation> _mutationObservers = new List<Observation>();

		private void NotifyObservers(Node target, string kind, string attributeName, List<Node> added, List<Node> removed)
		{
			if (_mutationObservers.Count == 0)
				return;
			foreach (Observation observation in _mutationObservers.ToArray())
			{
				bool matches = kind == "childList" ? observation.ChildList : kind == "attributes" ? observation.Attributes : observation.CharacterData;
				if (!matches)
					continue;
				if (kind == "attributes" && observation.AttributeFilter != null && !observation.AttributeFilter.Contains(attributeName))
					continue;
				bool inScope = observation.Target == target;
				if (!inScope && observation.Subtree)
				{
					for (Node n = target.Parent; n != null && !inScope; n = n.Parent)
						inScope = n == observation.Target;
				}
				if (!inScope)
					continue;
				JsObject record = Interpreter.NewObject();
				record.Put("type", kind);
				record.Put("target", Wrap(target));
				record.Put("addedNodes", WrapList(added));
				record.Put("removedNodes", WrapList(removed));
				record.Put("attributeName", attributeName == null ? JsValue.Null : attributeName);
				record.Put("attributeNamespace", JsValue.Null);
				record.Put("oldValue", JsValue.Null);
				record.Put("previousSibling", JsValue.Null);
				record.Put("nextSibling", JsValue.Null);
				observation.Pending ??= Interpreter.NewArray();
				observation.Pending.Elements.Add(record);
				if (!observation.Scheduled)
				{
					observation.Scheduled = true;
					Observation captured = observation;
					Interpreter.EnqueueJob(() => DeliverMutations(captured));
				}
			}
		}

		private void DeliverMutations(Observation observation)
		{
			observation.Scheduled = false;
			JsArray records = observation.Pending;
			observation.Pending = null;
			if (records == null || records.Elements.Count == 0)
				return;
			try
			{
				Interpreter.Call(observation.Callback, observation.Observer, new JsValue[] { records, observation.Observer });
			}
			catch (JsThrow t)
			{
				Interpreter.ReportError(t.Value);
			}
		}

		private JsArray WrapList(List<Node> nodes)
		{
			JsArray array = Interpreter.NewArray();
			if (nodes != null)
			{
				foreach (Node node in nodes)
					array.Elements.Add(Wrap(node));
			}
			return array;
		}

		private void SetupObservers()
		{
			JsObject mutationPrototype = Interpreter.NewObject();
			Interpreter.NewConstructor("MutationObserver", 1, mutationPrototype, null, (i, a, target) =>
			{
				JsFunction callback = Interpreter.RequireCallable(Interpreter.Arg(a, 0), "MutationObserver callback");
				JsObject observer = new JsObject(Interpreter.PrototypeFromConstructor(target, mutationPrototype)) { Class = "MutationObserver" };
				observer.Put("@@callback", callback, false);
				return observer;
			});
			Interpreter.DefineMethod(mutationPrototype, "observe", 2, (i, t, a) =>
			{
				Node node = Unwrap(Interpreter.Arg(a, 0));
				if (node == null)
					throw Interpreter.TypeError("MutationObserver.observe: parameter 1 is not a Node");
				JsValue options = Interpreter.Arg(a, 1);
				JsValue callback = t.Object.Get("@@callback", Interpreter);
				_mutationObservers.RemoveAll(o => o.Observer == t.Object && o.Target == node);
				Observation observation = new Observation
				{
					Observer = t.Object,
					Callback = callback.Function,
					Target = node,
					ChildList = options.IsObject && Interpreter.GetMember(options, "childList").Truthy,
					Attributes = options.IsObject && (Interpreter.GetMember(options, "attributes").Truthy || !Interpreter.GetMember(options, "attributeFilter").IsUndefined),
					CharacterData = options.IsObject && Interpreter.GetMember(options, "characterData").Truthy,
					Subtree = options.IsObject && Interpreter.GetMember(options, "subtree").Truthy,
				};
				if (options.IsObject && Interpreter.GetMember(options, "attributeFilter") is { IsObject: true } filter)
				{
					observation.AttributeFilter = new List<string>();
					foreach (JsValue name in Interpreter.Iterate(filter))
						observation.AttributeFilter.Add(name.ToJsString());
				}
				_mutationObservers.Add(observation);
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(mutationPrototype, "disconnect", 0, (i, t, a) =>
			{
				_mutationObservers.RemoveAll(o => o.Observer == t.Object);
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(mutationPrototype, "takeRecords", 0, (i, t, a) =>
			{
				JsArray records = Interpreter.NewArray();
				foreach (Observation observation in _mutationObservers)
				{
					if (observation.Observer == t.Object && observation.Pending != null)
					{
						records.Elements.AddRange(observation.Pending.Elements);
						observation.Pending = null;
					}
				}
				return records;
			});

			// Intersection and resize observers report once, straight away: everything on the page counts as visible and sized
			JsObject intersectionPrototype = Interpreter.NewObject();
			Interpreter.NewConstructor("IntersectionObserver", 1, intersectionPrototype, null, (i, a, target) =>
			{
				JsFunction callback = Interpreter.RequireCallable(Interpreter.Arg(a, 0), "IntersectionObserver callback");
				JsObject observer = new JsObject(Interpreter.PrototypeFromConstructor(target, intersectionPrototype)) { Class = "IntersectionObserver" };
				observer.Put("@@callback", callback, false);
				observer.Put("root", JsValue.Null);
				observer.Put("rootMargin", "0px 0px 0px 0px");
				observer.Put("thresholds", Interpreter.NewArray(new List<JsValue> { 0 }));
				return observer;
			});
			Interpreter.DefineMethod(intersectionPrototype, "observe", 1, (i, t, a) =>
			{
				Element element = Unwrap(Interpreter.Arg(a, 0)) as Element;
				if (element == null)
					return JsValue.Undefined;
				JsObject observer = t.Object;
				JsValue callback = observer.Get("@@callback", Interpreter);
				Interpreter.EnqueueJob(() =>
				{
					JsObject entry = Interpreter.NewObject();
					JsObject rect = BoundingRect(element);
					entry.Put("target", Wrap(element));
					entry.Put("isIntersecting", true);
					entry.Put("intersectionRatio", 1);
					entry.Put("boundingClientRect", rect);
					entry.Put("intersectionRect", rect);
					entry.Put("rootBounds", JsValue.Null);
					entry.Put("time", Now);
					Interpreter.Call(callback, observer, new JsValue[] { Interpreter.NewArray(new List<JsValue> { entry }), observer });
				});
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(intersectionPrototype, "unobserve", 1, (i, t, a) => JsValue.Undefined);
			Interpreter.DefineMethod(intersectionPrototype, "disconnect", 0, (i, t, a) => JsValue.Undefined);
			Interpreter.DefineMethod(intersectionPrototype, "takeRecords", 0, (i, t, a) => Interpreter.NewArray());

			JsObject resizePrototype = Interpreter.NewObject();
			Interpreter.NewConstructor("ResizeObserver", 1, resizePrototype, null, (i, a, target) =>
			{
				JsFunction callback = Interpreter.RequireCallable(Interpreter.Arg(a, 0), "ResizeObserver callback");
				JsObject observer = new JsObject(Interpreter.PrototypeFromConstructor(target, resizePrototype)) { Class = "ResizeObserver" };
				observer.Put("@@callback", callback, false);
				return observer;
			});
			Interpreter.DefineMethod(resizePrototype, "observe", 1, (i, t, a) =>
			{
				Element element = Unwrap(Interpreter.Arg(a, 0)) as Element;
				if (element == null)
					return JsValue.Undefined;
				JsObject observer = t.Object;
				JsValue callback = observer.Get("@@callback", Interpreter);
				Interpreter.EnqueueJob(() =>
				{
					JsObject rect = BoundingRect(element);
					JsObject size = Interpreter.NewObject();
					size.Put("inlineSize", rect.Get("width", Interpreter));
					size.Put("blockSize", rect.Get("height", Interpreter));
					JsObject entry = Interpreter.NewObject();
					entry.Put("target", Wrap(element));
					entry.Put("contentRect", rect);
					entry.Put("borderBoxSize", Interpreter.NewArray(new List<JsValue> { size }));
					entry.Put("contentBoxSize", Interpreter.NewArray(new List<JsValue> { size }));
					entry.Put("devicePixelContentBoxSize", Interpreter.NewArray(new List<JsValue> { size }));
					Interpreter.Call(callback, observer, new JsValue[] { Interpreter.NewArray(new List<JsValue> { entry }), observer });
				});
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(resizePrototype, "unobserve", 1, (i, t, a) => JsValue.Undefined);
			Interpreter.DefineMethod(resizePrototype, "disconnect", 0, (i, t, a) => JsValue.Undefined);
		}

		// Geometry, from the layout the host keeps

		private bool _layoutValid;
		public int ForcedLayouts;
		// Which accessors forced the layouts, for the probe's report
		public readonly Dictionary<string, int> ForcedLayoutsBy = new Dictionary<string, int>();
		internal string Accessor;
		// How many layouts one script, event or timer slice may force; past that it reads the geometry of the last one, since a full layout here costs what browsers spend on a frame
		public int ForcedLayoutLimit = 6;
		private int _forcedThisSlice;
		private bool _everLaidOut;

		private void NewSlice() => _forcedThisSlice = 0;

		internal LayoutBox LayoutBoxOf(Element element)
		{
			if (!_layoutValid || Dirty)
			{
				if (_everLaidOut && _forcedThisSlice >= ForcedLayoutLimit)
					return BoxFor?.Invoke(element);
				// A script that writes a style and reads a box in a loop forces a layout per round; the budget decides how many it gets
				Interpreter.CheckDeadline();
				ForcedLayouts++;
				_forcedThisSlice++;
				string accessor = Accessor ?? "other";
				ForcedLayoutsBy[accessor] = ForcedLayoutsBy.TryGetValue(accessor, out int count) ? count + 1 : 1;
				EnsureLayout?.Invoke();
				_layoutValid = true;
				_everLaidOut = true;
			}
			return BoxFor?.Invoke(element);
		}

		internal JsObject BoundingRect(Element element)
		{
			Accessor = "getBoundingClientRect";
			LayoutBox box = LayoutBoxOf(element);
			int scroll = ScrollY?.Invoke() ?? 0;
			double x = box?.X ?? 0;
			double y = (box?.Y ?? 0) - scroll;
			double width = box?.Width ?? 0;
			double height = box?.Height ?? 0;
			JsObject rect = Interpreter.NewObject();
			rect.Class = "DOMRect";
			rect.Put("x", x);
			rect.Put("y", y);
			rect.Put("width", width);
			rect.Put("height", height);
			rect.Put("top", y);
			rect.Put("left", x);
			rect.Put("right", x + width);
			rect.Put("bottom", y + height);
			Interpreter.DefineMethod(rect, "toJSON", 0, (i, t, a) => t);
			return rect;
		}
	}

	// A DOM event as scripts see it; the extra fields of mouse and keyboard events sit on the object as plain properties
	public sealed class JsEvent : JsObject
	{
		public string Type;
		public object Target;
		public object CurrentTarget;
		public int Phase;
		public bool Bubbles;
		public bool Cancelable;
		public bool Composed;
		public bool DefaultPrevented;
		public bool Stop;
		public bool StopImmediate;
		public bool Trusted;
		public bool Dispatching;
		public double TimeStamp;
		public JsValue Detail = JsValue.Null;

		public JsEvent(JsObject prototype) : base(prototype)
		{
			Class = "Event";
		}
	}
}
