using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Alloy.Sdk.Script;
using Alloy.Sdk.Web.Css;
using Alloy.Sdk.Web.Html;
using Alloy.Sdk.Web.Layout;
using Node = Alloy.Sdk.Web.Html.Node;

namespace Alloy.Sdk.Web.Dom
{
	// Web Storage: one map per origin, kept for the process; property access and the item methods both reach it
	public sealed class JsStorage : JsObject
	{
		public readonly Dictionary<string, string> Items;

		public JsStorage(JsObject prototype, Dictionary<string, string> items) : base(prototype)
		{
			Class = "Storage";
			Items = items;
		}

		public override JsProperty GetOwnProperty(string key)
		{
			if (!key.StartsWith("@@", StringComparison.Ordinal) && Items.TryGetValue(key, out string value))
				return new JsProperty { Value = value };
			return base.GetOwnProperty(key);
		}

		public override JsValue Get(string key, Interpreter interpreter, JsValue receiver)
		{
			if (!key.StartsWith("@@", StringComparison.Ordinal) && GetProperty(key) == null && Items.TryGetValue(key, out string value))
				return value;
			return base.Get(key, interpreter, receiver);
		}

		public override void Set(string key, JsValue value, Interpreter interpreter, JsValue receiver)
		{
			if (GetProperty(key) != null || key.StartsWith("@@", StringComparison.Ordinal))
				base.Set(key, value, interpreter, receiver);
			else
				Items[key] = value.ToJsString();
		}

		public override bool Delete(string key) => Items.Remove(key) || base.Delete(key);
	}

	// A list of name and value pairs: URLSearchParams, Headers and FormData share it
	public sealed class JsPairs : JsObject
	{
		public readonly List<KeyValuePair<string, string>> Pairs = new List<KeyValuePair<string, string>>();
		public JsUrl Owner;
		public readonly bool CaseInsensitive;

		public JsPairs(JsObject prototype, string cls, bool caseInsensitive) : base(prototype)
		{
			Class = cls;
			CaseInsensitive = caseInsensitive;
		}

		public string Normalize(string name) => CaseInsensitive ? name.ToLowerInvariant() : name;
	}

	public sealed class JsUrl : JsObject
	{
		public Uri Uri;
		public JsPairs SearchParams;

		public JsUrl(JsObject prototype, Uri uri) : base(prototype)
		{
			Class = "URL";
			Uri = uri;
		}
	}

	public sealed class JsResponse : JsObject
	{
		public byte[] Body = Array.Empty<byte>();
		public bool Used;

		public JsResponse(JsObject prototype) : base(prototype)
		{
			Class = "Response";
		}
	}

	public sealed class JsXhr : JsObject
	{
		public string Method = "GET";
		public Uri Target;
		public bool Async = true;
		public int ReadyState;
		public int Status;
		public string StatusText = "";
		public string ResponseText = "";
		public byte[] ResponseBody = Array.Empty<byte>();
		public string ResponseUrl = "";
		public string ResponseType = "";
		public Dictionary<string, string> ResponseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		public readonly List<KeyValuePair<string, string>> RequestHeaders = new List<KeyValuePair<string, string>>();
		public bool Aborted;

		public JsXhr(JsObject prototype) : base(prototype)
		{
			Class = "XMLHttpRequest";
		}
	}

	// window and what hangs off it
	public sealed partial class ScriptHost
	{
		private static readonly Dictionary<string, Dictionary<string, string>> s_localStorage = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
		private static readonly Dictionary<string, Dictionary<string, string>> s_sessionStorage = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
		public const string UserAgent = "Mozilla/5.0 (Alloy; x64) AppleWebKit/537.36 (KHTML, like Gecko) Alloy/0.1 Safari/537.36";
		private JsObject _location;
		private JsValue _historyState = JsValue.Null;
		private JsObject _urlPrototype;
		private JsObject _searchParamsPrototype;
		private JsObject _headersPrototype;
		private JsObject _formDataPrototype;
		private JsObject _responsePrototype;
		private JsObject _xhrPrototype;
		private readonly double _timeOrigin;
		private readonly Dictionary<string, JsValue> _customElements = new Dictionary<string, JsValue>(StringComparer.Ordinal);
		private readonly Dictionary<JsObject, string> _customElementTags = new Dictionary<JsObject, string>();

		public string Origin => Url == null || Url.Scheme != "http" && Url.Scheme != "https" ? "null" : Url.Scheme + "://" + Url.Host + (Url.IsDefaultPort ? "" : ":" + Url.Port);

		private void SetupDocument()
		{
			Interpreter.Global.Put("document", Wrap(Document), false);
		}

		private void SetupWindow()
		{
			JsObject window = Interpreter.Global;
			Interpreter interpreter = Interpreter;
			window.Put(Interpreter.SymbolToStringTag, "Window", false);
			foreach (string method in new[] { "addEventListener", "removeEventListener", "dispatchEvent" })
				window.Put(method, EventTargetPrototype.Get(method, interpreter), false);
			foreach (string name in HandlerNames)
			{
				string type = name;
				interpreter.DefineGetter(window, "on" + type, (i, t, a) => HandlerFor(window, type), (i, t, a) =>
				{
					SetHandler(window, type, Interpreter.Arg(a, 0));
					return JsValue.Undefined;
				});
			}
			window.Put("top", window, false);
			window.Put("parent", window, false);
			window.Put("frames", window, false);
			window.Put("frameElement", JsValue.Null, false);
			window.Put("opener", JsValue.Null);
			window.Put("closed", false, false);
			window.Put("name", "");
			window.Put("status", "");
			window.Put("length", 0, false);
			window.Put("devicePixelRatio", 1, false);
			window.Put("crossOriginIsolated", false, false);
			interpreter.DefineGetter(window, "innerWidth", (i, t, a) => ViewportWidth);
			interpreter.DefineGetter(window, "innerHeight", (i, t, a) => ViewportHeight);
			interpreter.DefineGetter(window, "outerWidth", (i, t, a) => ViewportWidth);
			interpreter.DefineGetter(window, "outerHeight", (i, t, a) => ViewportHeight + 100);
			interpreter.DefineGetter(window, "scrollX", (i, t, a) => 0);
			interpreter.DefineGetter(window, "pageXOffset", (i, t, a) => 0);
			interpreter.DefineGetter(window, "scrollY", (i, t, a) => ScrollY?.Invoke() ?? 0);
			interpreter.DefineGetter(window, "pageYOffset", (i, t, a) => ScrollY?.Invoke() ?? 0);
			interpreter.DefineGetter(window, "screenX", (i, t, a) => 0);
			interpreter.DefineGetter(window, "screenY", (i, t, a) => 0);
			interpreter.DefineGetter(window, "screenLeft", (i, t, a) => 0);
			interpreter.DefineGetter(window, "screenTop", (i, t, a) => 0);
			interpreter.DefineGetter(window, "isSecureContext", (i, t, a) => Url?.Scheme == "https");
			interpreter.DefineGetter(window, "origin", (i, t, a) => Origin);
			interpreter.DefineGetter(window, "event", (i, t, a) => _currentEvent == null ? JsValue.Undefined : _currentEvent);
			JsObject screen = interpreter.NewObject();
			screen.Class = "Screen";
			interpreter.DefineGetter(screen, "width", (i, t, a) => ViewportWidth);
			interpreter.DefineGetter(screen, "height", (i, t, a) => ViewportHeight);
			interpreter.DefineGetter(screen, "availWidth", (i, t, a) => ViewportWidth);
			interpreter.DefineGetter(screen, "availHeight", (i, t, a) => ViewportHeight);
			screen.Put("availLeft", 0);
			screen.Put("availTop", 0);
			screen.Put("colorDepth", 24);
			screen.Put("pixelDepth", 24);
			JsObject orientation = interpreter.NewObject();
			orientation.Put("type", "landscape-primary");
			orientation.Put("angle", 0);
			interpreter.DefineMethod(orientation, "addEventListener", 2, (i, t, a) => JsValue.Undefined);
			interpreter.DefineMethod(orientation, "removeEventListener", 2, (i, t, a) => JsValue.Undefined);
			screen.Put("orientation", orientation);
			window.Put("screen", screen, false);

			SetupLocation();
			SetupNavigator();
			SetupHistory();
			SetupDialogs();
			SetupStorage();
			SetupUrl();
			SetupFetch();
			SetupXhr();
			SetupObservers();
			SetupMisc();
		}

		private JsEvent _currentEvent;

		// location

		private void SetupLocation()
		{
			Interpreter interpreter = Interpreter;
			_location = interpreter.NewObject();
			_location.Class = "Location";
			foreach (string part in new[] { "href", "protocol", "host", "hostname", "port", "pathname", "search", "hash", "origin", "username", "password" })
			{
				string name = part;
				interpreter.DefineGetter(_location, name, (i, t, a) => name == "href" ? Url?.ToString() ?? "about:blank" : UrlPart(Url, name), name == "origin" ? null : (i, t, a) =>
				{
					string value = Interpreter.Arg(a, 0).ToJsString();
					if (name == "href")
						LocationAssign(value);
					else if (name == "hash")
						SetHash(value);
					else if (Url != null)
						LocationAssign(WithUrlPart(Url, name, value).ToString());
					return JsValue.Undefined;
				});
			}
			interpreter.DefineMethod(_location, "assign", 1, (i, t, a) =>
			{
				LocationAssign(Interpreter.Arg(a, 0).ToJsString());
				return JsValue.Undefined;
			});
			interpreter.DefineMethod(_location, "replace", 1, (i, t, a) =>
			{
				LocationAssign(Interpreter.Arg(a, 0).ToJsString());
				return JsValue.Undefined;
			});
			interpreter.DefineMethod(_location, "reload", 0, (i, t, a) =>
			{
				if (Url != null)
					Navigate?.Invoke(new FormSubmission { Url = Url, Method = "GET" });
				return JsValue.Undefined;
			});
			interpreter.DefineMethod(_location, "toString", 0, (i, t, a) => Url?.ToString() ?? "about:blank");
			interpreter.DefineMethod(_location, "toJSON", 0, (i, t, a) => Url?.ToString() ?? "about:blank");
			_location.Put("ancestorOrigins", interpreter.NewArray(), false);
			interpreter.DefineGetter(interpreter.Global, "location", (i, t, a) => _location, (i, t, a) =>
			{
				LocationAssign(Interpreter.Arg(a, 0).ToJsString());
				return JsValue.Undefined;
			});
		}

		internal void LocationAssign(string href)
		{
			Uri target = Resolve(href);
			if (target == null)
				return;
			if (Url != null && href.StartsWith("#", StringComparison.Ordinal))
			{
				SetHash(href);
				return;
			}
			if (Url != null && target.GetLeftPart(UriPartial.Query) == Url.GetLeftPart(UriPartial.Query) && target.Fragment.Length > 0)
			{
				SetHash(target.Fragment);
				return;
			}
			Navigate?.Invoke(new FormSubmission { Url = target, Method = "GET" });
		}

		internal void SetHash(string hash)
		{
			if (Url == null)
				return;
			string old = Url.ToString();
			UriBuilder builder = new UriBuilder(Url) { Fragment = hash.TrimStart('#') };
			Url = builder.Uri;
			if (old == Url.ToString())
				return;
			JsEvent ev = NewEvent("hashchange", false, false);
			ev.Put("oldURL", old);
			ev.Put("newURL", Url.ToString());
			DispatchEvent(ev, Interpreter.Global);
			string id = hash.TrimStart('#');
			if (id.Length > 0 && ElementById(id) is Element anchor)
			{
				LayoutBox box = LayoutBoxOf(anchor);
				if (box != null)
					ScrollTo?.Invoke((int)box.Y);
			}
		}

		internal static string UrlPart(Uri url, string name)
		{
			if (url == null)
				return "";
			switch (name)
			{
				case "href":
					return url.ToString();
				case "protocol":
					return url.Scheme + ":";
				case "host":
					return url.Host + (url.IsDefaultPort ? "" : ":" + url.Port);
				case "hostname":
					return url.Host;
				case "port":
					return url.IsDefaultPort ? "" : url.Port.ToString(CultureInfo.InvariantCulture);
				case "pathname":
					return url.AbsolutePath;
				case "search":
					return url.Query == "?" ? "" : url.Query;
				case "hash":
					return url.Fragment == "#" ? "" : url.Fragment;
				case "origin":
					return url.Scheme == "http" || url.Scheme == "https" ? url.Scheme + "://" + url.Host + (url.IsDefaultPort ? "" : ":" + url.Port) : "null";
				case "username":
					return url.UserInfo.Split(':')[0];
				case "password":
					return url.UserInfo.Contains(':') ? url.UserInfo.Substring(url.UserInfo.IndexOf(':') + 1) : "";
				default:
					return "";
			}
		}

		internal static Uri WithUrlPart(Uri url, string name, string value)
		{
			UriBuilder builder = new UriBuilder(url);
			switch (name)
			{
				case "protocol":
					builder.Scheme = value.TrimEnd(':');
					break;
				case "host":
				{
					int colon = value.LastIndexOf(':');
					if (colon > 0 && int.TryParse(value.Substring(colon + 1), out int port))
					{
						builder.Host = value.Substring(0, colon);
						builder.Port = port;
					}
					else
						builder.Host = value;
					break;
				}
				case "hostname":
					builder.Host = value;
					break;
				case "port":
					builder.Port = value.Length == 0 ? -1 : int.TryParse(value, out int p) ? p : builder.Port;
					break;
				case "pathname":
					builder.Path = value.StartsWith("/", StringComparison.Ordinal) ? value : "/" + value;
					break;
				case "search":
					builder.Query = value.TrimStart('?');
					break;
				case "hash":
					builder.Fragment = value.TrimStart('#');
					break;
				case "username":
					builder.UserName = value;
					break;
				case "password":
					builder.Password = value;
					break;
			}
			return builder.Uri;
		}

		// navigator

		private void SetupNavigator()
		{
			Interpreter interpreter = Interpreter;
			JsObject navigator = interpreter.NewObject();
			navigator.Class = "Navigator";
			navigator.Put("userAgent", UserAgent, false);
			navigator.Put("appName", "Netscape", false);
			navigator.Put("appCodeName", "Mozilla", false);
			navigator.Put("appVersion", UserAgent.Substring(8), false);
			navigator.Put("platform", "Alloy", false);
			navigator.Put("product", "Gecko", false);
			navigator.Put("productSub", "20030107", false);
			navigator.Put("vendor", "", false);
			navigator.Put("vendorSub", "", false);
			navigator.Put("language", "en-US", false);
			navigator.Put("languages", interpreter.NewStringArray(new[] { "en-US", "en" }), false);
			navigator.Put("onLine", true, false);
			navigator.Put("cookieEnabled", true, false);
			navigator.Put("doNotTrack", JsValue.Null, false);
			navigator.Put("hardwareConcurrency", 1, false);
			navigator.Put("deviceMemory", 1, false);
			navigator.Put("maxTouchPoints", 0, false);
			navigator.Put("pdfViewerEnabled", false, false);
			navigator.Put("webdriver", false, false);
			navigator.Put("plugins", EmptyCollection(), false);
			navigator.Put("mimeTypes", EmptyCollection(), false);
			interpreter.DefineMethod(navigator, "javaEnabled", 0, (i, t, a) => false);
			interpreter.DefineMethod(navigator, "vibrate", 1, (i, t, a) => false);
			interpreter.DefineMethod(navigator, "sendBeacon", 2, (i, t, a) =>
			{
				Uri target = Resolve(Interpreter.Arg(a, 0).ToJsString());
				if (target != null && Fetch != null)
					Fetch("POST", target, BodyBytes(Interpreter.Arg(a, 1), out string contentType), contentType ?? "text/plain;charset=UTF-8", null);
				return true;
			});
			interpreter.DefineMethod(navigator, "getBattery", 0, (i, t, a) =>
			{
				JsObject battery = interpreter.NewObject();
				battery.Put("charging", true);
				battery.Put("level", 1);
				battery.Put("chargingTime", 0);
				battery.Put("dischargingTime", double.PositiveInfinity);
				interpreter.DefineMethod(battery, "addEventListener", 2, (ii, tt, aa) => JsValue.Undefined);
				return interpreter.PromiseResolve(battery);
			});
			JsObject connection = interpreter.NewObject();
			connection.Put("effectiveType", "4g");
			connection.Put("type", "ethernet");
			connection.Put("downlink", 10);
			connection.Put("rtt", 50);
			connection.Put("saveData", false);
			interpreter.DefineMethod(connection, "addEventListener", 2, (i, t, a) => JsValue.Undefined);
			interpreter.DefineMethod(connection, "removeEventListener", 2, (i, t, a) => JsValue.Undefined);
			navigator.Put("connection", connection, false);
			JsObject clipboard = interpreter.NewObject();
			interpreter.DefineMethod(clipboard, "writeText", 1, (i, t, a) => interpreter.PromiseResolve(JsValue.Undefined));
			interpreter.DefineMethod(clipboard, "readText", 0, (i, t, a) => RejectedPromise("NotAllowedError", "Clipboard access is not available"));
			interpreter.DefineMethod(clipboard, "write", 1, (i, t, a) => interpreter.PromiseResolve(JsValue.Undefined));
			navigator.Put("clipboard", clipboard, false);
			JsObject geolocation = interpreter.NewObject();
			interpreter.DefineMethod(geolocation, "getCurrentPosition", 2, (i, t, a) =>
			{
				if (Interpreter.Arg(a, 1).IsCallable)
				{
					JsObject error = interpreter.NewObject();
					error.Put("code", 1);
					error.Put("message", "User denied Geolocation");
					JsFunction callback = a[1].Function;
					interpreter.EnqueueJob(() => interpreter.Call(callback, JsValue.Undefined, new JsValue[] { error }));
				}
				return JsValue.Undefined;
			});
			interpreter.DefineMethod(geolocation, "watchPosition", 2, (i, t, a) => 1);
			interpreter.DefineMethod(geolocation, "clearWatch", 1, (i, t, a) => JsValue.Undefined);
			navigator.Put("geolocation", geolocation, false);
			JsObject permissions = interpreter.NewObject();
			interpreter.DefineMethod(permissions, "query", 1, (i, t, a) =>
			{
				JsObject status = interpreter.NewObject();
				status.Put("state", "denied");
				status.Put("onchange", JsValue.Null);
				interpreter.DefineMethod(status, "addEventListener", 2, (ii, tt, aa) => JsValue.Undefined);
				return interpreter.PromiseResolve(status);
			});
			navigator.Put("permissions", permissions, false);
			JsObject storage = interpreter.NewObject();
			interpreter.DefineMethod(storage, "estimate", 0, (i, t, a) =>
			{
				JsObject estimate = interpreter.NewObject();
				estimate.Put("quota", 1073741824);
				estimate.Put("usage", 0);
				return interpreter.PromiseResolve(estimate);
			});
			interpreter.DefineMethod(storage, "persist", 0, (i, t, a) => interpreter.PromiseResolve(false));
			interpreter.DefineMethod(storage, "persisted", 0, (i, t, a) => interpreter.PromiseResolve(false));
			navigator.Put("storage", storage, false);
			JsObject mediaDevices = interpreter.NewObject();
			interpreter.DefineMethod(mediaDevices, "enumerateDevices", 0, (i, t, a) => interpreter.PromiseResolve(interpreter.NewArray()));
			interpreter.DefineMethod(mediaDevices, "getUserMedia", 1, (i, t, a) => RejectedPromise("NotFoundError", "No media devices"));
			navigator.Put("mediaDevices", mediaDevices, false);
			interpreter.DefineMethod(navigator, "share", 1, (i, t, a) => RejectedPromise("AbortError", "Share canceled"));
			interpreter.DefineMethod(navigator, "canShare", 1, (i, t, a) => false);
			interpreter.DefineMethod(navigator, "registerProtocolHandler", 2, (i, t, a) => JsValue.Undefined);
			interpreter.Global.Put("navigator", navigator, false);
			interpreter.Global.Put("clientInformation", navigator, false);
		}

		private JsArray EmptyCollection()
		{
			JsArray array = Interpreter.NewArray();
			Interpreter.DefineMethod(array, "item", 1, (i, t, a) => JsValue.Null);
			Interpreter.DefineMethod(array, "namedItem", 1, (i, t, a) => JsValue.Null);
			Interpreter.DefineMethod(array, "refresh", 0, (i, t, a) => JsValue.Undefined);
			return array;
		}

		private JsValue RejectedPromise(string name, string message)
		{
			JsPromise promise = Interpreter.NewPromise();
			Interpreter.RejectPromise(promise, DomException(name, message).Value);
			return promise;
		}

		// history

		private void SetupHistory()
		{
			Interpreter interpreter = Interpreter;
			JsObject history = interpreter.NewObject();
			history.Class = "History";
			history.Put("length", 1, false);
			history.Put("scrollRestoration", "auto");
			interpreter.DefineGetter(history, "state", (i, t, a) => _historyState);
			NativeFunction push = (i, t, a) =>
			{
				_historyState = Interpreter.Arg(a, 0);
				JsValue url = Interpreter.Arg(a, 2);
				if (!url.IsNullish)
				{
					Uri target = Resolve(url.ToJsString());
					if (target != null)
						Url = target;
				}
				return JsValue.Undefined;
			};
			interpreter.DefineMethod(history, "pushState", 3, push);
			interpreter.DefineMethod(history, "replaceState", 3, push);
			interpreter.DefineMethod(history, "back", 0, (i, t, a) =>
			{
				HistoryMove?.Invoke(-1);
				return JsValue.Undefined;
			});
			interpreter.DefineMethod(history, "forward", 0, (i, t, a) =>
			{
				HistoryMove?.Invoke(1);
				return JsValue.Undefined;
			});
			interpreter.DefineMethod(history, "go", 1, (i, t, a) =>
			{
				int delta = Interpreter.Arg(a, 0).IsUndefined ? 0 : a[0].ToInt32();
				if (delta == 0 && Url != null)
					Navigate?.Invoke(new FormSubmission { Url = Url, Method = "GET" });
				else
					HistoryMove?.Invoke(delta);
				return JsValue.Undefined;
			});
			interpreter.Global.Put("history", history, false);
		}

		// alert, confirm, prompt, open and the window methods that mean nothing here

		private void SetupDialogs()
		{
			Interpreter interpreter = Interpreter;
			JsObject window = interpreter.Global;
			interpreter.DefineMethod(window, "alert", 1, (i, t, a) =>
			{
				string text = Interpreter.Arg(a, 0).IsUndefined ? "" : a[0].ToJsString();
				Log("info", "alert: " + text);
				Alert?.Invoke(text);
				return JsValue.Undefined;
			});
			interpreter.DefineMethod(window, "confirm", 1, (i, t, a) =>
			{
				Log("info", "confirm: " + Interpreter.Arg(a, 0).ToJsString());
				return false;
			});
			interpreter.DefineMethod(window, "prompt", 2, (i, t, a) =>
			{
				Log("info", "prompt: " + Interpreter.Arg(a, 0).ToJsString());
				return JsValue.Null;
			});
			interpreter.DefineMethod(window, "open", 1, (i, t, a) =>
			{
				JsValue url = Interpreter.Arg(a, 0);
				if (!url.IsNullish && url.ToJsString().Length > 0)
					LocationAssign(url.ToJsString());
				return JsValue.Null;
			});
			foreach (string noop in new[] { "close", "focus", "blur", "print", "stop", "moveTo", "moveBy", "resizeTo", "resizeBy", "captureEvents", "releaseEvents" })
				interpreter.DefineMethod(window, noop, 0, (i, t, a) => JsValue.Undefined);
			NativeFunction scroll = (i, t, a) =>
			{
				JsValue first = Interpreter.Arg(a, 0);
				double y = first.IsObject ? Interpreter.GetMember(first, "top").ToNumber() : Interpreter.Arg(a, 1).ToNumber();
				if (!double.IsNaN(y))
					ScrollTo?.Invoke((int)y);
				return JsValue.Undefined;
			};
			interpreter.DefineMethod(window, "scrollTo", 2, scroll);
			interpreter.DefineMethod(window, "scroll", 2, scroll);
			interpreter.DefineMethod(window, "scrollBy", 2, (i, t, a) =>
			{
				JsValue first = Interpreter.Arg(a, 0);
				double dy = first.IsObject ? Interpreter.GetMember(first, "top").ToNumber() : Interpreter.Arg(a, 1).ToNumber();
				if (!double.IsNaN(dy))
					ScrollTo?.Invoke((ScrollY?.Invoke() ?? 0) + (int)dy);
				return JsValue.Undefined;
			});
			interpreter.DefineMethod(window, "getComputedStyle", 1, (i, t, a) =>
			{
				Element element = Unwrap(Interpreter.Arg(a, 0)) as Element;
				if (element == null)
					throw interpreter.TypeError("parameter 1 is not of type 'Element'.");
				return new JsComputedStyle(_stylePrototype, this, element);
			});
			interpreter.DefineMethod(window, "matchMedia", 1, (i, t, a) =>
			{
				string query = Interpreter.Arg(a, 0).ToJsString();
				JsObject list = interpreter.NewObject();
				list.Class = "MediaQueryList";
				bool matches;
				try
				{
					matches = MediaQuery.Matches(CssTokenizer.Tokenize(query), new MediaEnvironment(ViewportWidth, ViewportHeight));
				}
				catch (Exception)
				{
					matches = false;
				}
				list.Put("matches", matches);
				list.Put("media", query);
				list.Put("onchange", JsValue.Null);
				foreach (string method in new[] { "addListener", "removeListener", "addEventListener", "removeEventListener" })
					interpreter.DefineMethod(list, method, 2, (ii, tt, aa) => JsValue.Undefined);
				interpreter.DefineMethod(list, "dispatchEvent", 1, (ii, tt, aa) => true);
				return list;
			});
			interpreter.DefineMethod(window, "getSelection", 0, (i, t, a) => SelectionObject());
			interpreter.DefineMethod(window, "postMessage", 2, (i, t, a) =>
			{
				JsValue data = Interpreter.Arg(a, 0);
				interpreter.EnqueueJob(() =>
				{
					JsEvent ev = NewEvent("message", false, false);
					ev.Put("data", data);
					ev.Put("origin", Origin);
					ev.Put("source", window);
					ev.Put("ports", interpreter.NewArray());
					DispatchEvent(ev, window);
				});
				return JsValue.Undefined;
			});
			interpreter.DefineMethod(window, "requestIdleCallback", 1, (i, t, a) =>
			{
				JsValue callback = Interpreter.Arg(a, 0);
				JsObject deadline = interpreter.NewObject();
				deadline.Put("didTimeout", false);
				interpreter.DefineMethod(deadline, "timeRemaining", 0, (ii, tt, aa) => 50);
				JsValue timer = interpreter.Call(window.Get("setTimeout", interpreter), window, new JsValue[] { callback, 1, deadline });
				return timer;
			});
			window.Put("cancelIdleCallback", window.Get("clearTimeout", interpreter), false);
			interpreter.DefineMethod(window, "reportError", 1, (i, t, a) =>
			{
				interpreter.ReportError(Interpreter.Arg(a, 0));
				return JsValue.Undefined;
			});
			interpreter.DefineMethod(window, "btoa", 1, (i, t, a) =>
			{
				string text = Interpreter.Arg(a, 0).ToJsString();
				byte[] bytes = new byte[text.Length];
				for (int n = 0; n < text.Length; n++)
				{
					if (text[n] > 255)
						throw DomException("InvalidCharacterError", "The string to be encoded contains characters outside of the Latin1 range.");
					bytes[n] = (byte)text[n];
				}
				return Convert.ToBase64String(bytes);
			});
			interpreter.DefineMethod(window, "atob", 1, (i, t, a) =>
			{
				string text = Interpreter.Arg(a, 0).ToJsString().Replace(" ", "").Replace("\n", "").Replace("\r", "").Replace("\t", "");
				text = text.Replace('-', '+').Replace('_', '/');
				while (text.Length % 4 != 0)
					text += "=";
				try
				{
					byte[] bytes = Convert.FromBase64String(text);
					StringBuilder result = new StringBuilder(bytes.Length);
					foreach (byte b in bytes)
						result.Append((char)b);
					return result.ToString();
				}
				catch (FormatException)
				{
					throw DomException("InvalidCharacterError", "The string to be decoded is not correctly encoded.");
				}
			});
		}

		// localStorage, sessionStorage and cookies

		private void SetupStorage()
		{
			Interpreter interpreter = Interpreter;
			JsObject proto = NewPrototype(interpreter.ObjectPrototype, "Storage");
			interpreter.DefineMethod(proto, "getItem", 1, (i, t, a) => ThisStorage(t).Items.TryGetValue(Interpreter.Arg(a, 0).ToJsString(), out string value) ? value : JsValue.Null);
			interpreter.DefineMethod(proto, "setItem", 2, (i, t, a) =>
			{
				ThisStorage(t).Items[Interpreter.Arg(a, 0).ToJsString()] = Interpreter.Arg(a, 1).ToJsString();
				return JsValue.Undefined;
			});
			interpreter.DefineMethod(proto, "removeItem", 1, (i, t, a) =>
			{
				ThisStorage(t).Items.Remove(Interpreter.Arg(a, 0).ToJsString());
				return JsValue.Undefined;
			});
			interpreter.DefineMethod(proto, "clear", 0, (i, t, a) =>
			{
				ThisStorage(t).Items.Clear();
				return JsValue.Undefined;
			});
			interpreter.DefineMethod(proto, "key", 1, (i, t, a) =>
			{
				int index = Interpreter.Arg(a, 0).ToInt32();
				foreach (string key in ThisStorage(t).Items.Keys)
				{
					if (index-- == 0)
						return key;
				}
				return JsValue.Null;
			});
			interpreter.DefineGetter(proto, "length", (i, t, a) => ThisStorage(t).Items.Count);
			string origin = Origin;
			if (!s_localStorage.TryGetValue(origin, out Dictionary<string, string> local))
				s_localStorage[origin] = local = new Dictionary<string, string>(StringComparer.Ordinal);
			if (!s_sessionStorage.TryGetValue(origin, out Dictionary<string, string> session))
				s_sessionStorage[origin] = session = new Dictionary<string, string>(StringComparer.Ordinal);
			interpreter.Global.Put("localStorage", new JsStorage(proto, local), false);
			interpreter.Global.Put("sessionStorage", new JsStorage(proto, session), false);
		}

		private JsStorage ThisStorage(JsValue thisValue)
		{
			if (thisValue.IsObject && thisValue.Object is JsStorage storage)
				return storage;
			throw Interpreter.TypeError("Illegal invocation");
		}

		// document.cookie reads and writes the process's jar, minus what HttpOnly keeps from scripts
		internal string CookieHeader() => CookieJar.HeaderFor(Url, forScript: true) ?? "";

		internal void SetCookie(string text) => CookieJar.Store(Url, text, fromScript: true);

		// URL and URLSearchParams

		private void SetupUrl()
		{
			Interpreter interpreter = Interpreter;
			_urlPrototype = new JsObject(interpreter.ObjectPrototype);
			_urlPrototype.Put(Interpreter.SymbolToStringTag, "URL", false);
			JsFunction urlConstructor = interpreter.NewConstructor("URL", 1, _urlPrototype, null, (i, a, target) =>
			{
				Uri uri = ParseUrlArguments(a);
				if (uri == null)
					throw interpreter.TypeError("Failed to construct 'URL': Invalid URL");
				return new JsUrl(interpreter.PrototypeFromConstructor(target, _urlPrototype), uri);
			});
			interpreter.DefineMethod(urlConstructor, "canParse", 1, (i, t, a) => ParseUrlArguments(a) != null);
			interpreter.DefineMethod(urlConstructor, "parse", 1, (i, t, a) =>
			{
				Uri uri = ParseUrlArguments(a);
				return uri == null ? JsValue.Null : new JsUrl(_urlPrototype, uri);
			});
			interpreter.DefineMethod(urlConstructor, "createObjectURL", 1, (i, t, a) => "blob:" + Origin + "/" + RandomUuid());
			interpreter.DefineMethod(urlConstructor, "revokeObjectURL", 1, (i, t, a) => JsValue.Undefined);
			foreach (string part in new[] { "href", "protocol", "host", "hostname", "port", "pathname", "search", "hash", "origin", "username", "password" })
			{
				string name = part;
				interpreter.DefineGetter(_urlPrototype, name, (i, t, a) => UrlPart(ThisUrl(t).Uri, name), name == "origin" ? null : (i, t, a) =>
				{
					JsUrl url = ThisUrl(t);
					string value = Interpreter.Arg(a, 0).ToJsString();
					if (name == "href")
					{
						if (Uri.TryCreate(value, UriKind.Absolute, out Uri replaced))
							url.Uri = replaced;
					}
					else
						url.Uri = WithUrlPart(url.Uri, name, value);
					if (url.SearchParams != null)
						LoadPairs(url.SearchParams, UrlPart(url.Uri, "search"));
					return JsValue.Undefined;
				});
			}
			interpreter.DefineGetter(_urlPrototype, "searchParams", (i, t, a) =>
			{
				JsUrl url = ThisUrl(t);
				if (url.SearchParams == null)
				{
					url.SearchParams = new JsPairs(_searchParamsPrototype, "URLSearchParams", false) { Owner = url };
					LoadPairs(url.SearchParams, UrlPart(url.Uri, "search"));
				}
				return url.SearchParams;
			});
			interpreter.DefineMethod(_urlPrototype, "toString", 0, (i, t, a) => ThisUrl(t).Uri.ToString());
			interpreter.DefineMethod(_urlPrototype, "toJSON", 0, (i, t, a) => ThisUrl(t).Uri.ToString());

			_searchParamsPrototype = new JsObject(interpreter.ObjectPrototype);
			_searchParamsPrototype.Put(Interpreter.SymbolToStringTag, "URLSearchParams", false);
			interpreter.NewConstructor("URLSearchParams", 0, _searchParamsPrototype, null, (i, a, target) =>
			{
				JsPairs pairs = new JsPairs(interpreter.PrototypeFromConstructor(target, _searchParamsPrototype), "URLSearchParams", false);
				JsValue init = Interpreter.Arg(a, 0);
				if (init.IsString)
					LoadPairs(pairs, init.String);
				else if (init.IsObject)
					FillPairs(pairs, init);
				return pairs;
			});
			DefinePairMethods(_searchParamsPrototype, true);
			interpreter.DefineMethod(_searchParamsPrototype, "toString", 0, (i, t, a) => Forms.Encode(ThisPairs(t).Pairs));
			interpreter.DefineMethod(_searchParamsPrototype, "sort", 0, (i, t, a) =>
			{
				JsPairs pairs = ThisPairs(t);
				pairs.Pairs.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
				SyncOwner(pairs);
				return JsValue.Undefined;
			});
			interpreter.DefineGetter(_searchParamsPrototype, "size", (i, t, a) => ThisPairs(t).Pairs.Count);

			_headersPrototype = new JsObject(interpreter.ObjectPrototype);
			_headersPrototype.Put(Interpreter.SymbolToStringTag, "Headers", false);
			interpreter.NewConstructor("Headers", 0, _headersPrototype, null, (i, a, target) =>
			{
				JsPairs pairs = new JsPairs(interpreter.PrototypeFromConstructor(target, _headersPrototype), "Headers", true);
				if (Interpreter.Arg(a, 0).IsObject)
					FillPairs(pairs, a[0]);
				return pairs;
			});
			DefinePairMethods(_headersPrototype, false);

			_formDataPrototype = new JsObject(interpreter.ObjectPrototype);
			_formDataPrototype.Put(Interpreter.SymbolToStringTag, "FormData", false);
			interpreter.NewConstructor("FormData", 0, _formDataPrototype, null, (i, a, target) =>
			{
				JsPairs pairs = new JsPairs(interpreter.PrototypeFromConstructor(target, _formDataPrototype), "FormData", false);
				if (Unwrap(Interpreter.Arg(a, 0)) is Element form && form.TagName == "form")
					pairs.Pairs.AddRange(Forms.Collect(form, Unwrap(Interpreter.Arg(a, 1)) as Element));
				return pairs;
			});
			DefinePairMethods(_formDataPrototype, true);
		}

		private Uri ParseUrlArguments(JsValue[] arguments)
		{
			JsValue input = Interpreter.Arg(arguments, 0);
			string text = input.IsObject && input.Object is JsUrl inner ? inner.Uri.ToString() : input.ToJsString();
			JsValue baseValue = Interpreter.Arg(arguments, 1);
			if (!baseValue.IsUndefined)
			{
				string baseText = baseValue.IsObject && baseValue.Object is JsUrl baseUrl ? baseUrl.Uri.ToString() : baseValue.ToJsString();
				if (!Uri.TryCreate(baseText, UriKind.Absolute, out Uri baseUri))
					return null;
				return Uri.TryCreate(baseUri, text, out Uri relative) ? relative : null;
			}
			return Uri.TryCreate(text, UriKind.Absolute, out Uri absolute) && absolute.Scheme.Length > 1 ? absolute : null;
		}

		private JsUrl ThisUrl(JsValue thisValue)
		{
			if (thisValue.IsObject && thisValue.Object is JsUrl url)
				return url;
			throw Interpreter.TypeError("Illegal invocation");
		}

		private JsPairs ThisPairs(JsValue thisValue)
		{
			if (thisValue.IsObject && thisValue.Object is JsPairs pairs)
				return pairs;
			throw Interpreter.TypeError("Illegal invocation");
		}

		internal static void LoadPairs(JsPairs pairs, string query)
		{
			pairs.Pairs.Clear();
			if (query.StartsWith("?", StringComparison.Ordinal))
				query = query.Substring(1);
			foreach (string piece in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
			{
				int equals = piece.IndexOf('=');
				string name = equals < 0 ? piece : piece.Substring(0, equals);
				string value = equals < 0 ? "" : piece.Substring(equals + 1);
				pairs.Pairs.Add(new KeyValuePair<string, string>(FormDecode(name), FormDecode(value)));
			}
		}

		private static string FormDecode(string text)
		{
			try
			{
				return Uri.UnescapeDataString(text.Replace('+', ' '));
			}
			catch (Exception)
			{
				return text;
			}
		}

		private void FillPairs(JsPairs pairs, JsValue init)
		{
			if (init.Object is JsPairs other)
			{
				foreach (KeyValuePair<string, string> pair in other.Pairs)
					pairs.Pairs.Add(new KeyValuePair<string, string>(pairs.Normalize(pair.Key), pair.Value));
				return;
			}
			if (init.Object is JsArray || Interpreter.GetMember(init, Interpreter.SymbolIterator).IsCallable)
			{
				foreach (JsValue entry in Interpreter.Iterate(init))
					pairs.Pairs.Add(new KeyValuePair<string, string>(pairs.Normalize(Interpreter.GetMember(entry, "0").ToJsString()), Interpreter.GetMember(entry, "1").ToJsString()));
				return;
			}
			foreach (string key in Interpreter.OwnEnumerableKeys(init.Object))
				pairs.Pairs.Add(new KeyValuePair<string, string>(pairs.Normalize(key), init.Object.Get(key, Interpreter).ToJsString()));
		}

		private void SyncOwner(JsPairs pairs)
		{
			if (pairs.Owner == null)
				return;
			UriBuilder builder = new UriBuilder(pairs.Owner.Uri) { Query = Forms.Encode(pairs.Pairs) };
			pairs.Owner.Uri = builder.Uri;
		}

		private void DefinePairMethods(JsObject prototype, bool multi)
		{
			Interpreter interpreter = Interpreter;
			interpreter.DefineMethod(prototype, "append", 2, (i, t, a) =>
			{
				JsPairs pairs = ThisPairs(t);
				string name = pairs.Normalize(Interpreter.Arg(a, 0).ToJsString());
				string value = PairValue(Interpreter.Arg(a, 1));
				if (!multi && pairs.CaseInsensitive)
				{
					int index = pairs.Pairs.FindIndex(p => p.Key == name);
					if (index >= 0)
					{
						pairs.Pairs[index] = new KeyValuePair<string, string>(name, pairs.Pairs[index].Value + ", " + value);
						return JsValue.Undefined;
					}
				}
				pairs.Pairs.Add(new KeyValuePair<string, string>(name, value));
				SyncOwner(pairs);
				return JsValue.Undefined;
			});
			interpreter.DefineMethod(prototype, "set", 2, (i, t, a) =>
			{
				JsPairs pairs = ThisPairs(t);
				string name = pairs.Normalize(Interpreter.Arg(a, 0).ToJsString());
				string value = PairValue(Interpreter.Arg(a, 1));
				int index = pairs.Pairs.FindIndex(p => p.Key == name);
				pairs.Pairs.RemoveAll(p => p.Key == name);
				pairs.Pairs.Insert(index < 0 ? pairs.Pairs.Count : index, new KeyValuePair<string, string>(name, value));
				SyncOwner(pairs);
				return JsValue.Undefined;
			});
			interpreter.DefineMethod(prototype, "get", 1, (i, t, a) =>
			{
				JsPairs pairs = ThisPairs(t);
				string name = pairs.Normalize(Interpreter.Arg(a, 0).ToJsString());
				foreach (KeyValuePair<string, string> pair in pairs.Pairs)
				{
					if (pair.Key == name)
						return pair.Value;
				}
				return JsValue.Null;
			});
			interpreter.DefineMethod(prototype, "getAll", 1, (i, t, a) =>
			{
				JsPairs pairs = ThisPairs(t);
				string name = pairs.Normalize(Interpreter.Arg(a, 0).ToJsString());
				JsArray values = interpreter.NewArray();
				foreach (KeyValuePair<string, string> pair in pairs.Pairs)
				{
					if (pair.Key == name)
						values.Elements.Add(pair.Value);
				}
				return values;
			});
			interpreter.DefineMethod(prototype, "has", 1, (i, t, a) =>
			{
				JsPairs pairs = ThisPairs(t);
				string name = pairs.Normalize(Interpreter.Arg(a, 0).ToJsString());
				JsValue wanted = Interpreter.Arg(a, 1);
				foreach (KeyValuePair<string, string> pair in pairs.Pairs)
				{
					if (pair.Key == name && (wanted.IsUndefined || pair.Value == wanted.ToJsString()))
						return true;
				}
				return false;
			});
			interpreter.DefineMethod(prototype, "delete", 1, (i, t, a) =>
			{
				JsPairs pairs = ThisPairs(t);
				string name = pairs.Normalize(Interpreter.Arg(a, 0).ToJsString());
				JsValue wanted = Interpreter.Arg(a, 1);
				pairs.Pairs.RemoveAll(p => p.Key == name && (wanted.IsUndefined || p.Value == wanted.ToJsString()));
				SyncOwner(pairs);
				return JsValue.Undefined;
			});
			interpreter.DefineMethod(prototype, "forEach", 1, (i, t, a) =>
			{
				JsFunction callback = interpreter.RequireCallable(Interpreter.Arg(a, 0), "forEach callback");
				foreach (KeyValuePair<string, string> pair in ThisPairs(t).Pairs.ToArray())
					interpreter.Call(callback, Interpreter.Arg(a, 1), new JsValue[] { pair.Value, pair.Key, t });
				return JsValue.Undefined;
			});
			interpreter.DefineMethod(prototype, "entries", 0, (i, t, a) =>
			{
				JsArray entries = interpreter.NewArray();
				foreach (KeyValuePair<string, string> pair in ThisPairs(t).Pairs)
					entries.Elements.Add(interpreter.NewArray(new List<JsValue> { pair.Key, pair.Value }));
				return interpreter.Call(interpreter.ArrayValues, entries, Array.Empty<JsValue>());
			});
			interpreter.DefineMethod(prototype, "keys", 0, (i, t, a) =>
			{
				JsArray keys = interpreter.NewArray();
				foreach (KeyValuePair<string, string> pair in ThisPairs(t).Pairs)
					keys.Elements.Add(pair.Key);
				return interpreter.Call(interpreter.ArrayValues, keys, Array.Empty<JsValue>());
			});
			interpreter.DefineMethod(prototype, "values", 0, (i, t, a) =>
			{
				JsArray values = interpreter.NewArray();
				foreach (KeyValuePair<string, string> pair in ThisPairs(t).Pairs)
					values.Elements.Add(pair.Value);
				return interpreter.Call(interpreter.ArrayValues, values, Array.Empty<JsValue>());
			});
			prototype.Put(Interpreter.SymbolIterator, prototype.Get("entries", interpreter), false);
		}

		private string PairValue(JsValue value) => value.IsObject && value.Object is JsNode ? "" : value.ToJsString();

		// fetch

		private void SetupFetch()
		{
			Interpreter interpreter = Interpreter;
			_responsePrototype = new JsObject(interpreter.ObjectPrototype);
			_responsePrototype.Put(Interpreter.SymbolToStringTag, "Response", false);
			JsFunction responseConstructor = interpreter.NewConstructor("Response", 0, _responsePrototype, null, (i, a, target) =>
			{
				JsResponse response = new JsResponse(interpreter.PrototypeFromConstructor(target, _responsePrototype));
				response.Body = BodyBytes(Interpreter.Arg(a, 0), out string contentType) ?? Array.Empty<byte>();
				JsValue init = Interpreter.Arg(a, 1);
				int status = init.IsObject && !Interpreter.GetMember(init, "status").IsUndefined ? Interpreter.GetMember(init, "status").ToInt32() : 200;
				JsPairs headers = new JsPairs(_headersPrototype, "Headers", true);
				if (init.IsObject && Interpreter.GetMember(init, "headers").IsObject)
					FillPairs(headers, Interpreter.GetMember(init, "headers"));
				else if (contentType != null)
					headers.Pairs.Add(new KeyValuePair<string, string>("content-type", contentType));
				FillResponse(response, status, init.IsObject && Interpreter.GetMember(init, "statusText") is { IsString: true } statusText ? statusText.String : "", "", headers, false);
				return response;
			});
			interpreter.DefineMethod(responseConstructor, "json", 1, (i, t, a) =>
			{
				JsResponse response = new JsResponse(_responsePrototype);
				JsValue stringify = interpreter.Global.Get("JSON", interpreter).Object.Get("stringify", interpreter);
				response.Body = Encoding.UTF8.GetBytes(interpreter.Call(stringify, JsValue.Undefined, new[] { Interpreter.Arg(a, 0) }).ToJsString());
				JsPairs headers = new JsPairs(_headersPrototype, "Headers", true);
				headers.Pairs.Add(new KeyValuePair<string, string>("content-type", "application/json"));
				FillResponse(response, 200, "OK", "", headers, false);
				return response;
			});
			interpreter.DefineMethod(responseConstructor, "error", 0, (i, t, a) =>
			{
				JsResponse response = new JsResponse(_responsePrototype);
				FillResponse(response, 0, "", "", new JsPairs(_headersPrototype, "Headers", true), false);
				response.Put("type", "error");
				return response;
			});
			interpreter.DefineMethod(_responsePrototype, "text", 0, (i, t, a) => interpreter.PromiseResolve(Decode(ThisResponse(t).Body)));
			interpreter.DefineMethod(_responsePrototype, "json", 0, (i, t, a) =>
			{
				JsPromise promise = interpreter.NewPromise();
				try
				{
					JsValue parse = interpreter.Global.Get("JSON", interpreter).Object.Get("parse", interpreter);
					interpreter.ResolvePromise(promise, interpreter.Call(parse, JsValue.Undefined, new JsValue[] { Decode(ThisResponse(t).Body) }));
				}
				catch (JsThrow x)
				{
					interpreter.RejectPromise(promise, x.Value);
				}
				return promise;
			});
			interpreter.DefineMethod(_responsePrototype, "arrayBuffer", 0, (i, t, a) => interpreter.PromiseResolve(interpreter.NewArrayBuffer((byte[])ThisResponse(t).Body.Clone())));
			interpreter.DefineMethod(_responsePrototype, "bytes", 0, (i, t, a) => interpreter.PromiseResolve(interpreter.NewUint8Array((byte[])ThisResponse(t).Body.Clone())));
			interpreter.DefineMethod(_responsePrototype, "blob", 0, (i, t, a) =>
			{
				JsResponse response = ThisResponse(t);
				string type = (response.Get("headers", interpreter).Object as JsPairs)?.Pairs.Find(p => p.Key == "content-type").Value ?? "";
				return interpreter.PromiseResolve(NewBlob(response.Body, type));
			});
			interpreter.DefineMethod(_responsePrototype, "formData", 0, (i, t, a) =>
			{
				JsPairs pairs = new JsPairs(_formDataPrototype, "FormData", false);
				LoadPairs(pairs, Decode(ThisResponse(t).Body));
				return interpreter.PromiseResolve(pairs);
			});
			interpreter.DefineMethod(_responsePrototype, "clone", 0, (i, t, a) =>
			{
				JsResponse source = ThisResponse(t);
				JsResponse copy = new JsResponse(_responsePrototype) { Body = source.Body };
				foreach (string key in interpreter.OwnEnumerableKeys(source))
					copy.Put(key, source.Get(key, interpreter));
				return copy;
			});
			interpreter.DefineGetter(_responsePrototype, "bodyUsed", (i, t, a) => ThisResponse(t).Used);
			interpreter.DefineGetter(_responsePrototype, "body", (i, t, a) => JsValue.Null);

			JsObject requestPrototype = new JsObject(interpreter.ObjectPrototype);
			requestPrototype.Put(Interpreter.SymbolToStringTag, "Request", false);
			interpreter.NewConstructor("Request", 1, requestPrototype, null, (i, a, target) =>
			{
				JsObject request = new JsObject(interpreter.PrototypeFromConstructor(target, requestPrototype)) { Class = "Request" };
				JsValue input = Interpreter.Arg(a, 0);
				JsValue init = Interpreter.Arg(a, 1);
				if (input.IsObject && input.Object.Class == "Request")
				{
					foreach (string key in interpreter.OwnEnumerableKeys(input.Object))
						request.Put(key, input.Object.Get(key, interpreter));
				}
				else
					request.Put("url", Resolve(input.ToJsString())?.ToString() ?? input.ToJsString());
				request.Put("method", init.IsObject && !Interpreter.GetMember(init, "method").IsUndefined ? Interpreter.GetMember(init, "method").ToJsString().ToUpperInvariant() : request.Get("method", interpreter) is { IsString: true } m ? m : "GET");
				JsPairs headers = new JsPairs(_headersPrototype, "Headers", true);
				if (init.IsObject && Interpreter.GetMember(init, "headers").IsObject)
					FillPairs(headers, Interpreter.GetMember(init, "headers"));
				else if (request.Get("headers", interpreter).IsObject)
					FillPairs(headers, request.Get("headers", interpreter));
				request.Put("headers", headers);
				if (init.IsObject && !Interpreter.GetMember(init, "body").IsUndefined)
					request.Put("@@body", Interpreter.GetMember(init, "body"), false);
				foreach (string option in new[] { "mode", "credentials", "cache", "redirect", "referrer", "integrity", "signal" })
				{
					if (init.IsObject && !Interpreter.GetMember(init, option).IsUndefined)
						request.Put(option, Interpreter.GetMember(init, option));
					else if (request.Get(option, interpreter).IsUndefined)
						request.Put(option, option == "signal" ? JsValue.Null : option == "mode" ? "cors" : option == "credentials" ? "same-origin" : option == "cache" ? "default" : option == "redirect" ? "follow" : "");
				}
				interpreter.DefineMethod(request, "clone", 0, (ii, tt, aa) => tt);
				interpreter.DefineMethod(request, "text", 0, (ii, tt, aa) => interpreter.PromiseResolve(request.Get("@@body", interpreter).IsUndefined ? "" : request.Get("@@body", interpreter).ToJsString()));
				interpreter.DefineMethod(request, "json", 0, (ii, tt, aa) =>
				{
					JsValue parse = interpreter.Global.Get("JSON", interpreter).Object.Get("parse", interpreter);
					return interpreter.PromiseResolve(interpreter.Call(parse, JsValue.Undefined, new[] { request.Get("@@body", interpreter) }));
				});
				return request;
			});

			interpreter.DefineMethod(interpreter.Global, "fetch", 1, (i, t, a) =>
			{
				JsValue input = Interpreter.Arg(a, 0);
				JsValue init = Interpreter.Arg(a, 1);
				JsPromise promise = interpreter.NewPromise();
				string method = "GET";
				string url;
				JsValue body = JsValue.Undefined;
				JsPairs requestHeaders = null;
				if (input.IsObject && input.Object.Class == "Request")
				{
					url = input.Object.Get("url", interpreter).ToJsString();
					method = input.Object.Get("method", interpreter).ToJsString();
					body = input.Object.Get("@@body", interpreter);
					requestHeaders = input.Object.Get("headers", interpreter).Object as JsPairs;
				}
				else
					url = input.IsObject && input.Object is JsUrl jsUrl ? jsUrl.Uri.ToString() : input.ToJsString();
				if (init.IsObject)
				{
					if (!Interpreter.GetMember(init, "method").IsUndefined)
						method = Interpreter.GetMember(init, "method").ToJsString().ToUpperInvariant();
					if (!Interpreter.GetMember(init, "body").IsUndefined)
						body = Interpreter.GetMember(init, "body");
					if (Interpreter.GetMember(init, "headers").IsObject)
					{
						requestHeaders = new JsPairs(_headersPrototype, "Headers", true);
						FillPairs(requestHeaders, Interpreter.GetMember(init, "headers"));
					}
				}
				Uri target = Resolve(url);
				interpreter.EnqueueJob(() =>
				{
					if (target == null || Fetch == null || (target.Scheme != "http" && target.Scheme != "https"))
					{
						interpreter.RejectPromise(promise, interpreter.NewError("TypeError", "Failed to fetch"));
						return;
					}
					byte[] bytes = BodyBytes(body, out string contentType);
					string requestedType = requestHeaders?.Pairs.Find(p => p.Key == "content-type").Value;
					ResourceResponse result = Fetch(method, target, bytes, requestedType ?? contentType, requestHeaders?.Pairs);
					if (result == null || result.Status == 0)
					{
						interpreter.RejectPromise(promise, interpreter.NewError("TypeError", "Failed to fetch: " + (result?.Error ?? "no response")));
						return;
					}
					JsResponse response = new JsResponse(_responsePrototype) { Body = result.Body ?? Array.Empty<byte>() };
					JsPairs headers = new JsPairs(_headersPrototype, "Headers", true);
					foreach (KeyValuePair<string, string> header in result.Headers)
						headers.Pairs.Add(new KeyValuePair<string, string>(header.Key.ToLowerInvariant(), header.Value));
					FillResponse(response, result.Status, result.Reason ?? "", (result.Url ?? target).ToString(), headers, result.Url != null && result.Url != target);
					interpreter.ResolvePromise(promise, response);
				});
				return promise;
			});

			JsObject blobPrototype = new JsObject(interpreter.ObjectPrototype);
			blobPrototype.Put(Interpreter.SymbolToStringTag, "Blob", false);
			interpreter.NewConstructor("Blob", 0, blobPrototype, null, (i, a, target) =>
			{
				List<byte> bytes = new List<byte>();
				if (Interpreter.Arg(a, 0).IsObject)
				{
					foreach (JsValue part in interpreter.Iterate(a[0]))
						bytes.AddRange(BodyBytes(part, out _) ?? Array.Empty<byte>());
				}
				JsValue options = Interpreter.Arg(a, 1);
				JsObject blob = NewBlob(bytes.ToArray(), options.IsObject ? Interpreter.GetMember(options, "type").ToJsString() : "");
				blob.Prototype = interpreter.PrototypeFromConstructor(target, blobPrototype);
				return blob;
			});
			JsObject filePrototype = new JsObject(blobPrototype);
			filePrototype.Put(Interpreter.SymbolToStringTag, "File", false);
			interpreter.NewConstructor("File", 2, filePrototype, null, (i, a, target) =>
			{
				JsObject blob = interpreter.Construct(interpreter.Global.Get("Blob", interpreter), new[] { Interpreter.Arg(a, 0), Interpreter.Arg(a, 2) }).Object;
				blob.Prototype = interpreter.PrototypeFromConstructor(target, filePrototype);
				blob.Put("name", Interpreter.Arg(a, 1).ToJsString());
				blob.Put("lastModified", Interpreter.Now());
				return blob;
			});
			_blobPrototype = blobPrototype;

			JsObject abortSignalPrototype = NewPrototype(EventTargetPrototype, "AbortSignal");
			JsObject abortControllerPrototype = new JsObject(interpreter.ObjectPrototype);
			abortControllerPrototype.Put(Interpreter.SymbolToStringTag, "AbortController", false);
			JsFunction abortSignal = interpreter.Global.Get("AbortSignal", interpreter).Function;
			interpreter.DefineMethod(abortSignal, "timeout", 1, (i, t, a) => NewAbortSignal(abortSignalPrototype));
			interpreter.DefineMethod(abortSignal, "abort", 0, (i, t, a) =>
			{
				JsObject signal = NewAbortSignal(abortSignalPrototype);
				signal.Put("aborted", true);
				signal.Put("reason", Interpreter.Arg(a, 0));
				return signal;
			});
			interpreter.DefineMethod(abortSignal, "any", 1, (i, t, a) => NewAbortSignal(abortSignalPrototype));
			interpreter.NewConstructor("AbortController", 0, abortControllerPrototype, null, (i, a, target) =>
			{
				JsObject controller = new JsObject(interpreter.PrototypeFromConstructor(target, abortControllerPrototype)) { Class = "AbortController" };
				JsObject signal = NewAbortSignal(abortSignalPrototype);
				controller.Put("signal", signal, false);
				interpreter.DefineMethod(controller, "abort", 0, (ii, tt, aa) =>
				{
					if (signal.Get("aborted", interpreter).Truthy)
						return JsValue.Undefined;
					signal.Put("aborted", true);
					signal.Put("reason", Interpreter.Arg(aa, 0).IsUndefined ? DomException("AbortError", "signal is aborted without reason").Value : aa[0]);
					DispatchSimple("abort", signal, false, false);
					return JsValue.Undefined;
				});
				return controller;
			});
		}

		private JsObject _blobPrototype;

		private JsObject NewAbortSignal(JsObject prototype)
		{
			JsObject signal = new JsObject(prototype) { Class = "AbortSignal" };
			signal.Put("aborted", false);
			signal.Put("reason", JsValue.Undefined);
			signal.Put("onabort", JsValue.Null);
			Interpreter.DefineMethod(signal, "throwIfAborted", 0, (i, t, a) =>
			{
				if (signal.Get("aborted", Interpreter).Truthy)
					throw new JsThrow(signal.Get("reason", Interpreter));
				return JsValue.Undefined;
			});
			return signal;
		}

		private JsObject NewBlob(byte[] bytes, string type)
		{
			JsObject blob = new JsObject(_blobPrototype ?? Interpreter.ObjectPrototype) { Class = "Blob" };
			blob.Put("size", bytes.Length, false);
			blob.Put("type", type ?? "", false);
			Interpreter.DefineMethod(blob, "text", 0, (i, t, a) => Interpreter.PromiseResolve(Decode(bytes)));
			Interpreter.DefineMethod(blob, "arrayBuffer", 0, (i, t, a) => Interpreter.PromiseResolve(Interpreter.NewArrayBuffer((byte[])bytes.Clone())));
			Interpreter.DefineMethod(blob, "bytes", 0, (i, t, a) => Interpreter.PromiseResolve(Interpreter.NewUint8Array((byte[])bytes.Clone())));
			Interpreter.DefineMethod(blob, "slice", 2, (i, t, a) =>
			{
				int start = Interpreter.RelativeIndex(Interpreter.Arg(a, 0), bytes.Length, 0);
				int end = Interpreter.RelativeIndex(Interpreter.Arg(a, 1), bytes.Length, bytes.Length);
				byte[] slice = end > start ? bytes[start..end] : Array.Empty<byte>();
				return NewBlob(slice, Interpreter.Arg(a, 2).IsUndefined ? type : a[2].ToJsString());
			});
			Interpreter.DefineMethod(blob, "stream", 0, (i, t, a) => JsValue.Null);
			blob.Put("@@bytes", JsValue.Undefined, false);
			_blobBytes[blob] = bytes;
			return blob;
		}

		private readonly Dictionary<JsObject, byte[]> _blobBytes = new Dictionary<JsObject, byte[]>();

		// A request body as bytes, with the content type it implies
		internal byte[] BodyBytes(JsValue body, out string contentType)
		{
			contentType = null;
			if (body.IsNullish)
				return null;
			if (body.IsString)
			{
				contentType = "text/plain;charset=UTF-8";
				return Encoding.UTF8.GetBytes(body.String);
			}
			if (body.IsObject && body.Object is JsPairs pairs)
			{
				contentType = pairs.Class == "FormData" ? "application/x-www-form-urlencoded" : "application/x-www-form-urlencoded;charset=UTF-8";
				return Encoding.UTF8.GetBytes(Forms.Encode(pairs.Pairs));
			}
			if (body.IsObject && _blobBytes.TryGetValue(body.Object, out byte[] blobBytes))
			{
				string type = body.Object.Get("type", Interpreter).ToJsString();
				contentType = type.Length > 0 ? type : null;
				return blobBytes;
			}
			byte[] raw = Interpreter.BytesOf(body);
			if (raw != null)
				return raw;
			contentType = "text/plain;charset=UTF-8";
			return Encoding.UTF8.GetBytes(body.ToJsString());
		}

		private void FillResponse(JsResponse response, int status, string statusText, string url, JsPairs headers, bool redirected)
		{
			response.Put("status", status, false);
			response.Put("ok", status >= 200 && status < 300, false);
			response.Put("statusText", statusText ?? "", false);
			response.Put("url", url ?? "", false);
			response.Put("redirected", redirected, false);
			response.Put("type", "basic", false);
			response.Put("headers", headers, false);
		}

		private JsResponse ThisResponse(JsValue thisValue)
		{
			if (thisValue.IsObject && thisValue.Object is JsResponse response)
			{
				response.Used = true;
				return response;
			}
			throw Interpreter.TypeError("Illegal invocation");
		}

		// XMLHttpRequest

		private void SetupXhr()
		{
			Interpreter interpreter = Interpreter;
			_xhrPrototype = NewPrototype(EventTargetPrototype, "XMLHttpRequest");
			JsFunction constructor = interpreter.Global.Get("XMLHttpRequest", interpreter).Function;
			constructor.NativeNew = (i, a, target) =>
			{
				JsXhr xhr = new JsXhr(interpreter.PrototypeFromConstructor(target, _xhrPrototype));
				JsObject upload = new JsObject(EventTargetPrototype) { Class = "XMLHttpRequestUpload" };
				xhr.Put("upload", upload, false);
				xhr.Put("timeout", 0);
				xhr.Put("withCredentials", false);
				return xhr;
			};
			foreach (KeyValuePair<string, int> constant in new Dictionary<string, int> { { "UNSENT", 0 }, { "OPENED", 1 }, { "HEADERS_RECEIVED", 2 }, { "LOADING", 3 }, { "DONE", 4 } })
			{
				constructor.Put(constant.Key, constant.Value, false);
				_xhrPrototype.Put(constant.Key, constant.Value, false);
			}
			foreach (string name in new[] { "readystatechange", "load", "loadend", "loadstart", "error", "abort", "timeout", "progress" })
			{
				string type = name;
				interpreter.DefineGetter(_xhrPrototype, "on" + type, (i, t, a) => HandlerFor(t.Object, type), (i, t, a) =>
				{
					SetHandler(t.Object, type, Interpreter.Arg(a, 0));
					return JsValue.Undefined;
				});
			}
			interpreter.DefineGetter(_xhrPrototype, "readyState", (i, t, a) => ThisXhr(t).ReadyState);
			interpreter.DefineGetter(_xhrPrototype, "status", (i, t, a) => ThisXhr(t).Status);
			interpreter.DefineGetter(_xhrPrototype, "statusText", (i, t, a) => ThisXhr(t).StatusText);
			interpreter.DefineGetter(_xhrPrototype, "responseText", (i, t, a) => ThisXhr(t).ResponseText);
			interpreter.DefineGetter(_xhrPrototype, "responseURL", (i, t, a) => ThisXhr(t).ResponseUrl);
			interpreter.DefineGetter(_xhrPrototype, "responseXML", (i, t, a) => JsValue.Null);
			interpreter.DefineGetter(_xhrPrototype, "responseType", (i, t, a) => ThisXhr(t).ResponseType, (i, t, a) =>
			{
				ThisXhr(t).ResponseType = Interpreter.Arg(a, 0).ToJsString();
				return JsValue.Undefined;
			});
			interpreter.DefineGetter(_xhrPrototype, "response", (i, t, a) =>
			{
				JsXhr xhr = ThisXhr(t);
				switch (xhr.ResponseType)
				{
					case "json":
						try
						{
							JsValue parse = interpreter.Global.Get("JSON", interpreter).Object.Get("parse", interpreter);
							return interpreter.Call(parse, JsValue.Undefined, new JsValue[] { xhr.ResponseText });
						}
						catch (JsThrow)
						{
							return JsValue.Null;
						}
					case "arraybuffer":
						return interpreter.NewArrayBuffer((byte[])xhr.ResponseBody.Clone());
					case "blob":
						return NewBlob(xhr.ResponseBody, xhr.ResponseHeaders.TryGetValue("content-type", out string type) ? type : "");
					default:
						return xhr.ResponseText;
				}
			});
			interpreter.DefineMethod(_xhrPrototype, "open", 2, (i, t, a) =>
			{
				JsXhr xhr = ThisXhr(t);
				xhr.Method = Interpreter.Arg(a, 0).ToJsString().ToUpperInvariant();
				xhr.Target = Resolve(Interpreter.Arg(a, 1).ToJsString());
				xhr.Async = Interpreter.Arg(a, 2).IsUndefined || a[2].Truthy;
				xhr.RequestHeaders.Clear();
				xhr.Aborted = false;
				SetXhrState(xhr, 1);
				return JsValue.Undefined;
			});
			interpreter.DefineMethod(_xhrPrototype, "setRequestHeader", 2, (i, t, a) =>
			{
				ThisXhr(t).RequestHeaders.Add(new KeyValuePair<string, string>(Interpreter.Arg(a, 0).ToJsString(), Interpreter.Arg(a, 1).ToJsString()));
				return JsValue.Undefined;
			});
			interpreter.DefineMethod(_xhrPrototype, "overrideMimeType", 1, (i, t, a) => JsValue.Undefined);
			interpreter.DefineMethod(_xhrPrototype, "getResponseHeader", 1, (i, t, a) => ThisXhr(t).ResponseHeaders.TryGetValue(Interpreter.Arg(a, 0).ToJsString(), out string value) ? value : JsValue.Null);
			interpreter.DefineMethod(_xhrPrototype, "getAllResponseHeaders", 0, (i, t, a) =>
			{
				StringBuilder text = new StringBuilder();
				foreach (KeyValuePair<string, string> header in ThisXhr(t).ResponseHeaders)
					text.Append(header.Key.ToLowerInvariant()).Append(": ").Append(header.Value).Append("\r\n");
				return text.ToString();
			});
			interpreter.DefineMethod(_xhrPrototype, "abort", 0, (i, t, a) =>
			{
				JsXhr xhr = ThisXhr(t);
				xhr.Aborted = true;
				if (xhr.ReadyState > 0 && xhr.ReadyState < 4)
				{
					SetXhrState(xhr, 4);
					DispatchSimple("abort", xhr, false, false);
					DispatchSimple("loadend", xhr, false, false);
				}
				xhr.ReadyState = 0;
				return JsValue.Undefined;
			});
			interpreter.DefineMethod(_xhrPrototype, "send", 1, (i, t, a) =>
			{
				JsXhr xhr = ThisXhr(t);
				if (xhr.ReadyState != 1)
					throw DomException("InvalidStateError", "The object's state must be OPENED.");
				JsValue body = Interpreter.Arg(a, 0);
				Action perform = () =>
				{
					if (xhr.Aborted)
						return;
					DispatchSimple("loadstart", xhr, false, false);
					byte[] bytes = xhr.Method == "GET" || xhr.Method == "HEAD" ? null : BodyBytes(body, out _);
					string contentType = null;
					foreach (KeyValuePair<string, string> header in xhr.RequestHeaders)
					{
						if (header.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase))
							contentType = header.Value;
					}
					ResourceResponse result = xhr.Target != null && Fetch != null && (xhr.Target.Scheme == "http" || xhr.Target.Scheme == "https") ? Fetch(xhr.Method, xhr.Target, bytes, contentType ?? (bytes != null ? "text/plain;charset=UTF-8" : null), xhr.RequestHeaders) : null;
					if (xhr.Aborted)
						return;
					if (result == null || result.Status == 0)
					{
						xhr.Status = 0;
						SetXhrState(xhr, 4);
						DispatchSimple("error", xhr, false, false);
						DispatchSimple("loadend", xhr, false, false);
						return;
					}
					xhr.Status = result.Status;
					xhr.StatusText = result.Reason ?? "";
					xhr.ResponseHeaders = result.Headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
					xhr.ResponseBody = result.Body ?? Array.Empty<byte>();
					xhr.ResponseText = Decode(xhr.ResponseBody);
					xhr.ResponseUrl = (result.Url ?? xhr.Target).ToString();
					SetXhrState(xhr, 2);
					SetXhrState(xhr, 3);
					SetXhrState(xhr, 4);
					JsEvent progress = NewEvent("progress", false, false);
					progress.Put("lengthComputable", true);
					progress.Put("loaded", xhr.ResponseBody.Length);
					progress.Put("total", xhr.ResponseBody.Length);
					DispatchEvent(progress, xhr);
					DispatchSimple("load", xhr, false, false);
					DispatchSimple("loadend", xhr, false, false);
				};
				if (xhr.Async)
					interpreter.EnqueueJob(perform);
				else
					perform();
				return JsValue.Undefined;
			});
		}

		private void SetXhrState(JsXhr xhr, int state)
		{
			xhr.ReadyState = state;
			DispatchSimple("readystatechange", xhr, false, false);
		}

		private JsXhr ThisXhr(JsValue thisValue)
		{
			if (thisValue.IsObject && thisValue.Object is JsXhr xhr)
				return xhr;
			throw Interpreter.TypeError("Illegal invocation");
		}

		// performance, crypto, Intl, CSS, customElements, DOMParser, Image, Option and the other odds and ends

		private void SetupMisc()
		{
			Interpreter interpreter = Interpreter;
			JsObject window = interpreter.Global;
			double origin = Now;
			JsObject performance = interpreter.NewObject();
			performance.Class = "Performance";
			performance.Put("timeOrigin", origin);
			interpreter.DefineMethod(performance, "now", 0, (i, t, a) => Now - origin);
			interpreter.DefineMethod(performance, "mark", 1, (i, t, a) => PerformanceEntry(Interpreter.Arg(a, 0).ToJsString(), "mark", Now - origin, 0));
			interpreter.DefineMethod(performance, "measure", 1, (i, t, a) => PerformanceEntry(Interpreter.Arg(a, 0).ToJsString(), "measure", 0, Now - origin));
			foreach (string method in new[] { "getEntries", "getEntriesByType", "getEntriesByName" })
				interpreter.DefineMethod(performance, method, 1, (i, t, a) => interpreter.NewArray());
			foreach (string method in new[] { "clearMarks", "clearMeasures", "clearResourceTimings", "setResourceTimingBufferSize", "addEventListener", "removeEventListener" })
				interpreter.DefineMethod(performance, method, 1, (i, t, a) => JsValue.Undefined);
			interpreter.DefineMethod(performance, "toJSON", 0, (i, t, a) => interpreter.NewObject());
			JsObject timing = interpreter.NewObject();
			foreach (string field in new[] { "navigationStart", "fetchStart", "domainLookupStart", "domainLookupEnd", "connectStart", "connectEnd", "requestStart", "responseStart", "responseEnd", "domLoading", "domInteractive", "domContentLoadedEventStart", "domContentLoadedEventEnd", "domComplete", "loadEventStart", "loadEventEnd", "unloadEventStart", "unloadEventEnd", "redirectStart", "redirectEnd", "secureConnectionStart" })
				timing.Put(field, Math.Floor(interpreter.Now()));
			performance.Put("timing", timing);
			JsObject navigation = interpreter.NewObject();
			navigation.Put("type", 0);
			navigation.Put("redirectCount", 0);
			performance.Put("navigation", navigation);
			performance.Put("eventCounts", interpreter.NewObject());
			window.Put("performance", performance, false);
			JsObject performanceObserverPrototype = interpreter.NewObject();
			JsFunction performanceObserver = interpreter.NewConstructor("PerformanceObserver", 1, performanceObserverPrototype, null, (i, a, target) => new JsObject(interpreter.PrototypeFromConstructor(target, performanceObserverPrototype)) { Class = "PerformanceObserver" });
			performanceObserver.Put("supportedEntryTypes", interpreter.NewArray(), false);
			foreach (string method in new[] { "observe", "disconnect" })
				interpreter.DefineMethod(performanceObserverPrototype, method, 1, (i, t, a) => JsValue.Undefined);
			interpreter.DefineMethod(performanceObserverPrototype, "takeRecords", 0, (i, t, a) => interpreter.NewArray());

			JsObject crypto = interpreter.NewObject();
			crypto.Class = "Crypto";
			Random random = new Random();
			interpreter.DefineMethod(crypto, "getRandomValues", 1, (i, t, a) =>
			{
				if (Interpreter.Arg(a, 0).IsObject && a[0].Object is JsTypedArray typed)
				{
					byte[] bytes = new byte[typed.ByteLength];
					random.NextBytes(bytes);
					Array.Copy(bytes, 0, typed.Buffer.Data, typed.ByteOffset, bytes.Length);
				}
				else if (Interpreter.Arg(a, 0).IsObject && a[0].Object is JsArray array)
				{
					for (int n = 0; n < array.Elements.Count; n++)
						array.Elements[n] = random.Next(256);
				}
				return Interpreter.Arg(a, 0);
			});
			interpreter.DefineMethod(crypto, "randomUUID", 0, (i, t, a) => RandomUuid());
			JsObject subtle = interpreter.NewObject();
			foreach (string method in new[] { "digest", "encrypt", "decrypt", "sign", "verify", "generateKey", "importKey", "exportKey", "deriveKey", "deriveBits" })
				interpreter.DefineMethod(subtle, method, 2, (i, t, a) => RejectedPromise("NotSupportedError", "SubtleCrypto is not available"));
			crypto.Put("subtle", subtle, false);
			window.Put("crypto", crypto, false);

			SetupIntl();

			JsObject css = interpreter.NewObject();
			interpreter.DefineMethod(css, "supports", 1, (i, t, a) =>
			{
				string text = Interpreter.Arg(a, 0).ToJsString() + (a.Length > 1 ? ":" + a[1].ToJsString() : "");
				return !(text.Contains("grid-template-areas") && text.Contains("subgrid")) && !text.Contains("container-type") && !text.Contains("backdrop-filter") && !text.Contains(":has(");
			});
			interpreter.DefineMethod(css, "escape", 1, (i, t, a) =>
			{
				StringBuilder escaped = new StringBuilder();
				foreach (char c in Interpreter.Arg(a, 0).ToJsString())
				{
					if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c > 127)
						escaped.Append(c);
					else
						escaped.Append('\\').Append(c);
				}
				return escaped.ToString();
			});
			interpreter.DefineMethod(css, "registerProperty", 1, (i, t, a) => JsValue.Undefined);
			window.Put("CSS", css, false);
			JsObject stylesheetPrototype = new JsObject(interpreter.ObjectPrototype);
			interpreter.NewConstructor("CSSStyleSheet", 0, stylesheetPrototype, null, (i, a, target) =>
			{
				JsObject sheet = new JsObject(interpreter.PrototypeFromConstructor(target, stylesheetPrototype)) { Class = "CSSStyleSheet" };
				sheet.Put("cssRules", interpreter.NewArray());
				interpreter.DefineMethod(sheet, "replaceSync", 1, (ii, tt, aa) => JsValue.Undefined);
				interpreter.DefineMethod(sheet, "replace", 1, (ii, tt, aa) => interpreter.PromiseResolve(sheet));
				interpreter.DefineMethod(sheet, "insertRule", 1, (ii, tt, aa) => 0);
				interpreter.DefineMethod(sheet, "deleteRule", 1, (ii, tt, aa) => JsValue.Undefined);
				return sheet;
			});
			Wrap(Document).Object.Put("adoptedStyleSheets", interpreter.NewArray());

			JsObject customElements = interpreter.NewObject();
			customElements.Class = "CustomElementRegistry";
			interpreter.DefineMethod(customElements, "define", 2, (i, t, a) =>
			{
				string name = Interpreter.Arg(a, 0).ToJsString().ToLowerInvariant();
				JsValue ctor = Interpreter.Arg(a, 1);
				if (!name.Contains('-'))
					throw DomException("SyntaxError", "\"" + name + "\" is not a valid custom element name");
				if (!ctor.IsCallable || !ctor.Function.IsConstructor)
					throw interpreter.TypeError("The element constructor is not a constructor");
				if (_customElements.ContainsKey(name))
					throw DomException("NotSupportedError", "the name \"" + name + "\" has already been used with this registry");
				_customElements[name] = ctor;
				_customElementTags[ctor.Object] = name;
				// Elements already in the document with that name take on the class now
				foreach (Element element in new List<Element>(Document.DescendantElements()))
				{
					if (element.TagName != name)
						continue;
					try
					{
						ConstructRegistered(name, element);
					}
					catch (JsThrow x)
					{
						interpreter.ReportError(x.Value);
						continue;
					}
					// Defined from an imported module, the connected callbacks wait for the importing module's top level, which often supplies what they await; an await cannot suspend here
					if (interpreter.InDependencyModule)
					{
						Element upgraded = element;
						interpreter.EnqueueJob(() => CustomCallback(upgraded, "connectedCallback"));
					}
					else
						CustomCallback(element, "connectedCallback");
				}
				return JsValue.Undefined;
			});
			interpreter.DefineMethod(customElements, "get", 1, (i, t, a) => _customElements.TryGetValue(Interpreter.Arg(a, 0).ToJsString(), out JsValue ctor) ? ctor : JsValue.Undefined);
			interpreter.DefineMethod(customElements, "whenDefined", 1, (i, t, a) => interpreter.PromiseResolve(JsValue.Undefined));
			interpreter.DefineMethod(customElements, "upgrade", 1, (i, t, a) => JsValue.Undefined);
			window.Put("customElements", customElements, false);

			JsObject parserPrototype = interpreter.NewObject();
			interpreter.NewConstructor("DOMParser", 0, parserPrototype, null, (i, a, target) => new JsObject(interpreter.PrototypeFromConstructor(target, parserPrototype)) { Class = "DOMParser" });
			interpreter.DefineMethod(parserPrototype, "parseFromString", 2, (i, t, a) => Wrap(HtmlParser.Parse(Interpreter.Arg(a, 0).ToJsString())));
			JsObject serializerPrototype = interpreter.NewObject();
			interpreter.NewConstructor("XMLSerializer", 0, serializerPrototype, null, (i, a, target) => new JsObject(interpreter.PrototypeFromConstructor(target, serializerPrototype)) { Class = "XMLSerializer" });
			interpreter.DefineMethod(serializerPrototype, "serializeToString", 1, (i, t, a) => Unwrap(Interpreter.Arg(a, 0))?.OuterHtml ?? "");

			JsFunction image = interpreter.NewNative("Image", 2, (i, t, a) => throw interpreter.TypeError("Constructor Image requires 'new'"));
			image.NativeNew = (i, a, target) =>
			{
				Element img = new Element("img");
				if (!Interpreter.Arg(a, 0).IsUndefined)
					img.SetAttribute("width", a[0].ToJsString());
				if (!Interpreter.Arg(a, 1).IsUndefined)
					img.SetAttribute("height", a[1].ToJsString());
				return Wrap(img).Object;
			};
			image.Put("prototype", PrototypeForTag("img", "HTMLImageElement"), false);
			window.Put("Image", image, false);
			JsFunction option = interpreter.NewNative("Option", 4, (i, t, a) => throw interpreter.TypeError("Constructor Option requires 'new'"));
			option.NativeNew = (i, a, target) =>
			{
				Element element = new Element("option");
				if (!Interpreter.Arg(a, 0).IsUndefined)
					element.AppendChild(new TextNode(a[0].ToJsString()));
				if (!Interpreter.Arg(a, 1).IsUndefined)
					element.SetAttribute("value", a[1].ToJsString());
				if (Interpreter.Arg(a, 3).Truthy || Interpreter.Arg(a, 2).Truthy)
					element.SetAttribute("selected", "");
				return Wrap(element).Object;
			};
			option.Put("prototype", PrototypeForTag("option", "HTMLOptionElement"), false);
			window.Put("Option", option, false);
			JsFunction audio = interpreter.NewNative("Audio", 1, (i, t, a) => throw interpreter.TypeError("Constructor Audio requires 'new'"));
			audio.NativeNew = (i, a, target) =>
			{
				Element element = new Element("audio");
				if (!Interpreter.Arg(a, 0).IsUndefined)
					element.SetAttribute("src", a[0].ToJsString());
				return Wrap(element).Object;
			};
			window.Put("Audio", audio, false);

			JsObject encoderPrototype = interpreter.NewObject();
			JsFunction encoder = interpreter.NewConstructor("TextEncoder", 0, encoderPrototype, null, (i, a, target) =>
			{
				JsObject result = new JsObject(interpreter.PrototypeFromConstructor(target, encoderPrototype)) { Class = "TextEncoder" };
				result.Put("encoding", "utf-8", false);
				return result;
			});
			interpreter.DefineMethod(encoderPrototype, "encode", 1, (i, t, a) => interpreter.NewUint8Array(Encoding.UTF8.GetBytes(Interpreter.Arg(a, 0).IsUndefined ? "" : a[0].ToJsString())));
			interpreter.DefineMethod(encoderPrototype, "encodeInto", 2, (i, t, a) =>
			{
				byte[] bytes = Encoding.UTF8.GetBytes(Interpreter.Arg(a, 0).ToJsString());
				JsObject result = interpreter.NewObject();
				int written = 0;
				if (Interpreter.Arg(a, 1).IsObject && a[1].Object is JsTypedArray target)
				{
					written = Math.Min(bytes.Length, target.ByteLength);
					Array.Copy(bytes, 0, target.Buffer.Data, target.ByteOffset, written);
				}
				result.Put("read", Interpreter.Arg(a, 0).ToJsString().Length);
				result.Put("written", written);
				return result;
			});
			JsObject decoderPrototype = interpreter.NewObject();
			interpreter.NewConstructor("TextDecoder", 0, decoderPrototype, null, (i, a, target) =>
			{
				JsObject result = new JsObject(interpreter.PrototypeFromConstructor(target, decoderPrototype)) { Class = "TextDecoder" };
				result.Put("encoding", "utf-8", false);
				return result;
			});
			interpreter.DefineMethod(decoderPrototype, "decode", 1, (i, t, a) =>
			{
				byte[] bytes = interpreter.BytesOf(Interpreter.Arg(a, 0));
				return bytes == null ? "" : Decode(bytes);
			});
			_ = encoder;

			JsObject notification = interpreter.NewObject();
			JsFunction notificationConstructor = interpreter.NewConstructor("Notification", 1, notification, null, (i, a, target) => new JsObject(interpreter.PrototypeFromConstructor(target, notification)) { Class = "Notification" });
			notificationConstructor.Put("permission", "denied", false);
			interpreter.DefineMethod(notificationConstructor, "requestPermission", 0, (i, t, a) => interpreter.PromiseResolve("denied"));

			JsObject webSocketPrototype = NewPrototype(EventTargetPrototype, "WebSocket");
			JsFunction webSocket = interpreter.Global.Get("WebSocket", interpreter).Function;
			webSocket.NativeNew = (i, a, target) =>
			{
				JsObject socket = new JsObject(interpreter.PrototypeFromConstructor(target, webSocketPrototype)) { Class = "WebSocket" };
				socket.Put("url", Interpreter.Arg(a, 0).ToJsString());
				socket.Put("readyState", 3);
				socket.Put("protocol", "");
				socket.Put("bufferedAmount", 0);
				foreach (string handler in new[] { "onopen", "onclose", "onerror", "onmessage" })
					socket.Put(handler, JsValue.Null);
				interpreter.DefineMethod(socket, "send", 1, (ii, tt, aa) => throw DomException("InvalidStateError", "WebSocket is not open"));
				interpreter.DefineMethod(socket, "close", 0, (ii, tt, aa) => JsValue.Undefined);
				interpreter.EnqueueJob(() =>
				{
					DispatchSimple("error", socket, false, false);
					JsValue onerror = socket.Get("onerror", interpreter);
					if (onerror.IsCallable)
						interpreter.Call(onerror, socket, new JsValue[] { NewEvent("error", false, false) });
					JsEvent close = NewEvent("close", false, false);
					close.Put("code", 1006);
					close.Put("reason", "WebSockets are not available");
					close.Put("wasClean", false);
					DispatchEvent(close, socket);
					JsValue onclose = socket.Get("onclose", interpreter);
					if (onclose.IsCallable)
						interpreter.Call(onclose, socket, new JsValue[] { close });
				});
				return socket;
			};
			foreach (KeyValuePair<string, int> constant in new Dictionary<string, int> { { "CONNECTING", 0 }, { "OPEN", 1 }, { "CLOSING", 2 }, { "CLOSED", 3 } })
				webSocket.Put(constant.Key, constant.Value, false);

			JsObject visualViewport = new JsObject(EventTargetPrototype) { Class = "VisualViewport" };
			interpreter.DefineGetter(visualViewport, "width", (i, t, a) => ViewportWidth);
			interpreter.DefineGetter(visualViewport, "height", (i, t, a) => ViewportHeight);
			visualViewport.Put("offsetLeft", 0);
			visualViewport.Put("offsetTop", 0);
			visualViewport.Put("pageLeft", 0);
			interpreter.DefineGetter(visualViewport, "pageTop", (i, t, a) => ScrollY?.Invoke() ?? 0);
			visualViewport.Put("scale", 1);
			window.Put("visualViewport", visualViewport, false);

			JsObject scheduler = interpreter.NewObject();
			interpreter.DefineMethod(scheduler, "postTask", 1, (i, t, a) =>
			{
				JsValue callback = Interpreter.Arg(a, 0);
				JsPromise promise = interpreter.NewPromise();
				interpreter.EnqueueJob(() =>
				{
					try
					{
						interpreter.ResolvePromise(promise, interpreter.Call(callback, JsValue.Undefined, Array.Empty<JsValue>()));
					}
					catch (JsThrow x)
					{
						interpreter.RejectPromise(promise, x.Value);
					}
				});
				return promise;
			});
			interpreter.DefineMethod(scheduler, "yield", 0, (i, t, a) => interpreter.PromiseResolve(JsValue.Undefined));
			window.Put("scheduler", scheduler, false);

			JsObject domExceptionPrototype = new JsObject(interpreter.ErrorPrototype);
			domExceptionPrototype.Put("name", "Error", false);
			JsFunction domException = interpreter.NewConstructor("DOMException", 2, domExceptionPrototype, null, (i, a, target) =>
			{
				JsObject error = interpreter.NewError("Error", Interpreter.Arg(a, 0).IsUndefined ? "" : a[0].ToJsString());
				error.Prototype = interpreter.PrototypeFromConstructor(target, domExceptionPrototype);
				error.Put("name", Interpreter.Arg(a, 1).IsUndefined ? "Error" : a[1].ToJsString(), false);
				error.Put("code", 0, false);
				return error;
			});
			foreach (KeyValuePair<string, int> code in new Dictionary<string, int> { { "INDEX_SIZE_ERR", 1 }, { "HIERARCHY_REQUEST_ERR", 3 }, { "NOT_FOUND_ERR", 8 }, { "NOT_SUPPORTED_ERR", 9 }, { "INVALID_STATE_ERR", 11 }, { "SYNTAX_ERR", 12 }, { "SECURITY_ERR", 18 }, { "NETWORK_ERR", 19 }, { "ABORT_ERR", 20 }, { "QUOTA_EXCEEDED_ERR", 22 }, { "TIMEOUT_ERR", 23 } })
				domException.Put(code.Key, code.Value, false);

			window.Put("onerror", JsValue.Null);
		}

		// A version 4 UUID from the runtime's random source; the Win32 layer has no CoCreateGuid
		private static string RandomUuid()
		{
			byte[] bytes = new byte[16];
			new Random().NextBytes(bytes);
			bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
			bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
			string hex = Convert.ToHexString(bytes).ToLowerInvariant();
			return hex.Substring(0, 8) + "-" + hex.Substring(8, 4) + "-" + hex.Substring(12, 4) + "-" + hex.Substring(16, 4) + "-" + hex.Substring(20);
		}

		private JsObject PerformanceEntry(string name, string type, double start, double duration)
		{
			JsObject entry = Interpreter.NewObject();
			entry.Put("name", name);
			entry.Put("entryType", type);
			entry.Put("startTime", start);
			entry.Put("duration", duration);
			Interpreter.DefineMethod(entry, "toJSON", 0, (i, t, a) => t);
			return entry;
		}

		private void SetupIntl()
		{
			Interpreter interpreter = Interpreter;
			JsObject intl = interpreter.NewObject();
			intl.Class = "Intl";
			interpreter.Global.Put("Intl", intl, false);
			JsObject dateFormatPrototype = interpreter.NewObject();
			JsFunction dateFormat = NewIntlConstructor(intl, "DateTimeFormat", dateFormatPrototype);
			interpreter.DefineMethod(dateFormatPrototype, "format", 1, (i, t, a) =>
			{
				JsValue date = Interpreter.Arg(a, 0);
				if (date.IsUndefined)
					date = interpreter.Construct(interpreter.Global.Get("Date", interpreter), Array.Empty<JsValue>());
				else if (!date.IsObject)
					date = interpreter.Construct(interpreter.Global.Get("Date", interpreter), new[] { date });
				JsValue options = t.Object.Get("@@options", interpreter);
				bool withTime = options.IsObject && (!Interpreter.GetMember(options, "hour").IsUndefined || Interpreter.GetMember(options, "timeStyle").IsString);
				bool withDate = !options.IsObject || !Interpreter.GetMember(options, "year").IsUndefined || !Interpreter.GetMember(options, "month").IsUndefined || !Interpreter.GetMember(options, "day").IsUndefined || Interpreter.GetMember(options, "dateStyle").IsString || !withTime;
				string method = withDate && withTime ? "toLocaleString" : withTime ? "toLocaleTimeString" : "toLocaleDateString";
				return interpreter.Call(interpreter.GetMember(date, method), date, Array.Empty<JsValue>());
			});
			interpreter.DefineMethod(dateFormatPrototype, "formatToParts", 1, (i, t, a) =>
			{
				JsObject part = interpreter.NewObject();
				part.Put("type", "literal");
				part.Put("value", interpreter.Call(t.Object.Get("format", interpreter), t, a));
				return interpreter.NewArray(new List<JsValue> { part });
			});
			interpreter.DefineMethod(dateFormatPrototype, "formatRange", 2, (i, t, a) => interpreter.Call(t.Object.Get("format", interpreter), t, new[] { Interpreter.Arg(a, 0) }).ToJsString() + " - " + interpreter.Call(t.Object.Get("format", interpreter), t, new[] { Interpreter.Arg(a, 1) }).ToJsString());
			_ = dateFormat;
			JsObject numberFormatPrototype = interpreter.NewObject();
			NewIntlConstructor(intl, "NumberFormat", numberFormatPrototype);
			interpreter.DefineMethod(numberFormatPrototype, "format", 1, (i, t, a) =>
			{
				double n = Interpreter.Arg(a, 0).ToNumber();
				JsValue options = t.Object.Get("@@options", interpreter);
				string style = options.IsObject ? Interpreter.GetMember(options, "style").ToJsString() : "decimal";
				int minimum = options.IsObject && Interpreter.GetMember(options, "minimumFractionDigits").IsNumber ? (int)Interpreter.GetMember(options, "minimumFractionDigits").Number : -1;
				int maximum = options.IsObject && Interpreter.GetMember(options, "maximumFractionDigits").IsNumber ? (int)Interpreter.GetMember(options, "maximumFractionDigits").Number : -1;
				if (style == "percent")
					n *= 100;
				string text;
				if (!double.IsFinite(n))
					text = JsValue.NumberToString(n);
				else if (minimum >= 0 || maximum >= 0)
				{
					int digits = maximum >= 0 ? maximum : Math.Max(minimum, 3);
					text = Math.Round(n, Math.Min(15, digits), MidpointRounding.AwayFromZero).ToString("#,##0." + new string('0', Math.Max(0, minimum)) + new string('#', Math.Max(0, digits - Math.Max(0, minimum))), CultureInfo.InvariantCulture).TrimEnd('.');
				}
				else if (style == "currency")
					text = n.ToString("#,##0.00", CultureInfo.InvariantCulture);
				else
					text = n == Math.Floor(n) && Math.Abs(n) < 1e21 ? n.ToString("#,##0", CultureInfo.InvariantCulture) : n.ToString("#,##0.###", CultureInfo.InvariantCulture);
				if (style == "currency")
				{
					string currency = options.IsObject ? Interpreter.GetMember(options, "currency").ToJsString().ToUpperInvariant() : "USD";
					string symbol = currency == "USD" ? "$" : currency == "EUR" ? "€" : currency == "GBP" ? "£" : currency == "JPY" ? "¥" : currency + " ";
					text = (n < 0 ? "-" : "") + symbol + text.TrimStart('-');
				}
				else if (style == "percent")
					text += "%";
				return text;
			});
			interpreter.DefineMethod(numberFormatPrototype, "formatToParts", 1, (i, t, a) =>
			{
				JsObject part = interpreter.NewObject();
				part.Put("type", "integer");
				part.Put("value", interpreter.Call(t.Object.Get("format", interpreter), t, a));
				return interpreter.NewArray(new List<JsValue> { part });
			});
			JsObject pluralPrototype = interpreter.NewObject();
			NewIntlConstructor(intl, "PluralRules", pluralPrototype);
			interpreter.DefineMethod(pluralPrototype, "select", 1, (i, t, a) => Interpreter.Arg(a, 0).ToNumber() == 1 ? "one" : "other");
			JsObject relativePrototype = interpreter.NewObject();
			NewIntlConstructor(intl, "RelativeTimeFormat", relativePrototype);
			interpreter.DefineMethod(relativePrototype, "format", 2, (i, t, a) =>
			{
				double value = Interpreter.Arg(a, 0).ToNumber();
				string unit = Interpreter.Arg(a, 1).ToJsString().TrimEnd('s');
				string count = Math.Abs(value) + " " + unit + (Math.Abs(value) == 1 ? "" : "s");
				return value < 0 ? count + " ago" : "in " + count;
			});
			JsObject collatorPrototype = interpreter.NewObject();
			NewIntlConstructor(intl, "Collator", collatorPrototype);
			interpreter.DefineMethod(collatorPrototype, "compare", 2, (i, t, a) => Math.Sign(string.Compare(Interpreter.Arg(a, 0).ToJsString(), Interpreter.Arg(a, 1).ToJsString(), StringComparison.InvariantCultureIgnoreCase)));
			JsObject listPrototype = interpreter.NewObject();
			NewIntlConstructor(intl, "ListFormat", listPrototype);
			interpreter.DefineMethod(listPrototype, "format", 1, (i, t, a) =>
			{
				List<string> items = new List<string>();
				foreach (JsValue item in interpreter.Iterate(Interpreter.Arg(a, 0)))
					items.Add(item.ToJsString());
				if (items.Count <= 1)
					return items.Count == 0 ? "" : items[0];
				return string.Join(", ", items.GetRange(0, items.Count - 1)) + ", and " + items[items.Count - 1];
			});
			JsObject displayNamesPrototype = interpreter.NewObject();
			NewIntlConstructor(intl, "DisplayNames", displayNamesPrototype);
			interpreter.DefineMethod(displayNamesPrototype, "of", 1, (i, t, a) => Interpreter.Arg(a, 0).ToJsString());
			JsObject segmenterPrototype = interpreter.NewObject();
			NewIntlConstructor(intl, "Segmenter", segmenterPrototype);
			interpreter.DefineMethod(segmenterPrototype, "segment", 1, (i, t, a) =>
			{
				JsArray segments = interpreter.NewArray();
				string text = Interpreter.Arg(a, 0).ToJsString();
				int index = 0;
				foreach (JsValue piece in interpreter.Iterate(text))
				{
					JsObject segment = interpreter.NewObject();
					segment.Put("segment", piece);
					segment.Put("index", index);
					segment.Put("input", text);
					index += piece.ToJsString().Length;
					segments.Elements.Add(segment);
				}
				return segments;
			});
			interpreter.DefineMethod(intl, "getCanonicalLocales", 1, (i, t, a) => Interpreter.Arg(a, 0).IsString ? interpreter.NewArray(new List<JsValue> { a[0] }) : Interpreter.Arg(a, 0).IsObject ? interpreter.NewArray(interpreter.Iterate(a[0])) : interpreter.NewArray());
			interpreter.DefineMethod(intl, "supportedValuesOf", 1, (i, t, a) => interpreter.NewArray());
		}

		private JsFunction NewIntlConstructor(JsObject intl, string name, JsObject prototype)
		{
			Interpreter interpreter = Interpreter;
			prototype.Put(Interpreter.SymbolToStringTag, "Intl." + name, false);
			JsFunction constructor = interpreter.NewConstructor(name, 0, prototype, (i, t, a) => NewIntlObject(prototype, a), (i, a, target) => NewIntlObject(interpreter.PrototypeFromConstructor(target, prototype), a).Object);
			interpreter.Global.Delete(name);
			intl.Put(name, constructor, false);
			interpreter.DefineMethod(constructor, "supportedLocalesOf", 1, (i, t, a) => interpreter.NewArray(new List<JsValue> { "en-US" }));
			interpreter.DefineMethod(prototype, "resolvedOptions", 0, (i, t, a) =>
			{
				JsObject options = interpreter.NewObject();
				options.Put("locale", "en-US");
				options.Put("timeZone", "UTC");
				options.Put("calendar", "gregory");
				options.Put("numberingSystem", "latn");
				JsValue given = t.Object.Get("@@options", interpreter);
				if (given.IsObject)
					interpreter.CopyDataProperties(options, given);
				options.Put("timeZone", "UTC");
				return options;
			});
			return constructor;
		}

		private JsValue NewIntlObject(JsObject prototype, JsValue[] arguments)
		{
			JsObject result = new JsObject(prototype) { Class = "Intl" };
			result.Put("@@options", Interpreter.Arg(arguments, 1), false);
			return result;
		}
	}
}
