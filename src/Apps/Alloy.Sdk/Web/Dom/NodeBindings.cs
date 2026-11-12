using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Alloy.Sdk.Script;
using Alloy.Sdk.Web.Css;
using Alloy.Sdk.Web.Html;
using Node = Alloy.Sdk.Web.Html.Node;
using Alloy.Sdk.Web.Layout;

namespace Alloy.Sdk.Web.Dom
{
	// A DOM node as a script object; one per node, kept for the page's life
	public sealed class JsNode : JsObject
	{
		public readonly Node Node;

		public JsNode(JsObject prototype, Node node, string cls) : base(prototype)
		{
			Node = node;
			Class = cls;
		}
	}

	// Node, Element, Document, Text and the collections, styles and token lists hanging off them
	public sealed partial class ScriptHost
	{
		public const string FragmentTag = "#document-fragment";

		internal JsObject EventTargetPrototype;
		internal JsObject NodePrototype;
		internal JsObject ElementPrototype;
		internal JsObject HtmlElementPrototype;
		internal JsObject DocumentPrototype;
		internal JsObject FragmentPrototype;
		internal JsObject ShadowRootPrototype;
		internal JsObject TextPrototype;
		internal JsObject CommentPrototype;
		internal JsObject EventPrototype;
		internal JsObject MouseEventPrototype;
		internal JsObject KeyboardEventPrototype;
		internal JsObject InputEventPrototype;
		internal JsObject CustomEventPrototype;
		private readonly Dictionary<string, JsObject> _tagPrototypes = new Dictionary<string, JsObject>(StringComparer.Ordinal);
		private readonly Dictionary<Node, JsStyle> _styles = new Dictionary<Node, JsStyle>();

		private delegate JsValue NodeReader(Node node);

		private delegate void NodeWriter(Node node, JsValue value);

		private delegate JsValue ElementReader(Element element);

		private delegate void ElementWriter(Element element, JsValue value);

		internal static readonly string[] HandlerNames =
		{
			"click", "dblclick", "mousedown", "mouseup", "mousemove", "mouseover", "mouseout", "mouseenter", "mouseleave", "contextmenu", "wheel", "auxclick",
			"keydown", "keyup", "keypress", "input", "change", "submit", "reset", "focus", "blur", "focusin", "focusout", "load", "unload", "beforeunload", "error", "abort",
			"resize", "scroll", "hashchange", "popstate", "pageshow", "pagehide", "visibilitychange", "readystatechange", "touchstart", "touchend", "touchmove", "touchcancel",
			"pointerdown", "pointerup", "pointermove", "pointerover", "pointerout", "pointerenter", "pointerleave", "pointercancel", "animationend", "animationstart", "animationiteration",
			"transitionend", "transitionstart", "message", "online", "offline", "storage", "select", "toggle", "invalid", "play", "pause", "ended", "canplay", "canplaythrough",
			"loadeddata", "loadedmetadata", "loadstart", "progress", "timeupdate", "volumechange", "drag", "dragstart", "dragend", "dragover", "dragenter", "dragleave", "drop",
			"copy", "cut", "paste", "search", "languagechange", "DOMContentLoaded", "selectionchange", "securitypolicyviolation", "rejectionhandled", "unhandledrejection",
		};

		// Wrapping

		public JsValue Wrap(Node node)
		{
			if (node == null)
				return JsValue.Null;
			if (_wrappers.TryGetValue(node, out JsNode existing))
				return existing;
			JsNode wrapper;
			switch (node)
			{
				case Html.Document:
					wrapper = new JsNode(DocumentPrototype, node, "HTMLDocument");
					break;
				case Element element when element.TagName == FragmentTag:
					wrapper = new JsNode(FragmentPrototype, node, "DocumentFragment");
					break;
				case Element element when element.TagName == Element.ShadowRootTag:
					wrapper = new JsNode(ShadowRootPrototype, node, "ShadowRoot");
					break;
				case Element element:
				{
					string interfaceName = InterfaceFor(element.TagName);
					wrapper = new JsNode(PrototypeForTag(element.TagName, interfaceName), node, interfaceName);
					break;
				}
				case TextNode:
					wrapper = new JsNode(TextPrototype, node, "Text");
					break;
				default:
					wrapper = new JsNode(CommentPrototype, node, "Comment");
					break;
			}
			_wrappers[node] = wrapper;
			return wrapper;
		}

		internal Node Unwrap(JsValue value) => value.IsObject && value.Object is JsNode wrapper ? wrapper.Node : null;

		private Element ThisElement(JsValue thisValue)
		{
			if (thisValue.IsObject && thisValue.Object is JsNode { Node: Element element })
				return element;
			throw Interpreter.TypeError("Illegal invocation");
		}

		private Node ThisNode(JsValue thisValue)
		{
			if (thisValue.IsObject && thisValue.Object is JsNode wrapper)
				return wrapper.Node;
			throw Interpreter.TypeError("Illegal invocation");
		}

		private static string InterfaceFor(string tag)
		{
			switch (tag)
			{
				case "a": return "HTMLAnchorElement";
				case "img": return "HTMLImageElement";
				case "input": return "HTMLInputElement";
				case "button": return "HTMLButtonElement";
				case "form": return "HTMLFormElement";
				case "select": return "HTMLSelectElement";
				case "option": return "HTMLOptionElement";
				case "textarea": return "HTMLTextAreaElement";
				case "label": return "HTMLLabelElement";
				case "script": return "HTMLScriptElement";
				case "style": return "HTMLStyleElement";
				case "link": return "HTMLLinkElement";
				case "meta": return "HTMLMetaElement";
				case "title": return "HTMLTitleElement";
				case "body": return "HTMLBodyElement";
				case "head": return "HTMLHeadElement";
				case "html": return "HTMLHtmlElement";
				case "div": return "HTMLDivElement";
				case "span": return "HTMLSpanElement";
				case "p": return "HTMLParagraphElement";
				case "h1": case "h2": case "h3": case "h4": case "h5": case "h6": return "HTMLHeadingElement";
				case "ul": return "HTMLUListElement";
				case "ol": return "HTMLOListElement";
				case "li": return "HTMLLIElement";
				case "table": return "HTMLTableElement";
				case "tr": return "HTMLTableRowElement";
				case "td": case "th": return "HTMLTableCellElement";
				case "thead": case "tbody": case "tfoot": return "HTMLTableSectionElement";
				case "iframe": return "HTMLIFrameElement";
				case "canvas": return "HTMLCanvasElement";
				case "template": return "HTMLTemplateElement";
				case "video": return "HTMLVideoElement";
				case "audio": return "HTMLAudioElement";
				case "br": return "HTMLBRElement";
				case "hr": return "HTMLHRElement";
				case "pre": return "HTMLPreElement";
				case "nav": case "section": case "article": case "aside": case "header": case "footer": case "main": case "figure": case "figcaption": case "address": case "summary": return "HTMLElement";
				case "details": return "HTMLDetailsElement";
				case "dialog": return "HTMLDialogElement";
				case "em": case "strong": case "b": case "i": case "u": case "s": case "small": case "code": case "sub": case "sup": case "mark": case "abbr": case "cite": case "q": case "kbd": case "dfn": case "var": case "samp": case "time": case "bdi": case "bdo": case "wbr": case "noscript": case "data": return "HTMLElement";
				case "svg": case "path": case "g": case "circle": case "rect": case "line": case "polyline": case "polygon": case "use": case "defs": case "symbol": case "text": case "ellipse": case "clippath": case "mask": case "lineargradient": case "stop": case "filter": case "tspan": return "SVGElement";
				case "fieldset": return "HTMLFieldSetElement";
				case "legend": return "HTMLLegendElement";
				case "picture": return "HTMLPictureElement";
				case "source": return "HTMLSourceElement";
				case "object": return "HTMLObjectElement";
				case "embed": return "HTMLEmbedElement";
				case "area": return "HTMLAreaElement";
				case "map": return "HTMLMapElement";
				case "base": return "HTMLBaseElement";
				case "dl": return "HTMLDListElement";
				case "dt": case "dd": return "HTMLElement";
				case "blockquote": return "HTMLQuoteElement";
				case "progress": return "HTMLProgressElement";
				case "meter": return "HTMLMeterElement";
				case "output": return "HTMLOutputElement";
				case "optgroup": return "HTMLOptGroupElement";
				case "datalist": return "HTMLDataListElement";
				case "caption": return "HTMLTableCaptionElement";
				case "col": case "colgroup": return "HTMLTableColElement";
				case "slot": return "HTMLSlotElement";
				case "menu": return "HTMLMenuElement";
				case "font": return "HTMLFontElement";
				case "center": return "HTMLElement";
				default:
					return tag.Contains('-') ? "HTMLElement" : "HTMLUnknownElement";
			}
		}

		private static readonly string[] InterfaceNames =
		{
			"HTMLAnchorElement", "HTMLImageElement", "HTMLInputElement", "HTMLButtonElement", "HTMLFormElement", "HTMLSelectElement", "HTMLOptionElement", "HTMLTextAreaElement", "HTMLLabelElement",
			"HTMLScriptElement", "HTMLStyleElement", "HTMLLinkElement", "HTMLMetaElement", "HTMLTitleElement", "HTMLBodyElement", "HTMLHeadElement", "HTMLHtmlElement", "HTMLDivElement", "HTMLSpanElement",
			"HTMLParagraphElement", "HTMLHeadingElement", "HTMLUListElement", "HTMLOListElement", "HTMLLIElement", "HTMLTableElement", "HTMLTableRowElement", "HTMLTableCellElement", "HTMLTableSectionElement",
			"HTMLIFrameElement", "HTMLCanvasElement", "HTMLTemplateElement", "HTMLVideoElement", "HTMLAudioElement", "HTMLMediaElement", "HTMLBRElement", "HTMLHRElement", "HTMLPreElement", "HTMLDetailsElement",
			"HTMLDialogElement", "SVGElement", "SVGSVGElement", "HTMLFieldSetElement", "HTMLLegendElement", "HTMLPictureElement", "HTMLSourceElement", "HTMLObjectElement", "HTMLEmbedElement", "HTMLAreaElement",
			"HTMLMapElement", "HTMLBaseElement", "HTMLDListElement", "HTMLQuoteElement", "HTMLProgressElement", "HTMLMeterElement", "HTMLOutputElement", "HTMLOptGroupElement", "HTMLDataListElement",
			"HTMLTableCaptionElement", "HTMLTableColElement", "HTMLSlotElement", "HTMLMenuElement", "HTMLFontElement", "HTMLUnknownElement", "HTMLModElement", "HTMLTimeElement", "HTMLDataElement",
		};

		private JsObject PrototypeForTag(string tag, string interfaceName)
		{
			if (_tagPrototypes.TryGetValue(interfaceName, out JsObject prototype))
				return prototype;
			// SVG elements share HTMLElement's surface here (style, dataset, focus), which is close enough for pages that touch inline SVG
			JsObject parent = HtmlElementPrototype;
			if (interfaceName == "HTMLElement")
				return HtmlElementPrototype;
			prototype = new JsObject(parent);
			prototype.Put(Interpreter.SymbolToStringTag, interfaceName, false);
			Interpreter.NewConstructor(interfaceName, 0, prototype, (i, t, a) => throw Interpreter.TypeError("Illegal constructor"), (i, a, target) => ConstructCustomElement(target, prototype));
			_tagPrototypes[interfaceName] = prototype;
			return prototype;
		}

		private void Getter(JsObject prototype, string name, NodeReader getter, NodeWriter setter = null)
		{
			Interpreter.DefineGetter(prototype, name, (i, t, a) => getter(ThisNode(t)), setter == null ? null : (i, t, a) =>
			{
				setter(ThisNode(t), Interpreter.Arg(a, 0));
				return JsValue.Undefined;
			});
		}

		private void ElementGetter(JsObject prototype, string name, ElementReader getter, ElementWriter setter = null)
		{
			Interpreter.DefineGetter(prototype, name, (i, t, a) => getter(ThisElement(t)), setter == null ? null : (i, t, a) =>
			{
				setter(ThisElement(t), Interpreter.Arg(a, 0));
				return JsValue.Undefined;
			});
		}

		private void Attribute(JsObject prototype, string property, string attribute, string fallback = "")
		{
			ElementGetter(prototype, property, e => e.GetAttribute(attribute) ?? fallback, (e, v) => SetAttribute(e, attribute, v.ToJsString()));
		}

		private void BooleanAttribute(JsObject prototype, string property, string attribute)
		{
			ElementGetter(prototype, property, e => e.HasAttribute(attribute), (e, v) =>
			{
				if (v.Truthy)
					SetAttribute(e, attribute, "");
				else
					RemoveAttribute(e, attribute);
			});
		}

		private void UrlAttribute(JsObject prototype, string property, string attribute)
		{
			ElementGetter(prototype, property, e =>
			{
				string value = e.GetAttribute(attribute);
				if (value == null)
					return "";
				Uri resolved = Resolve(value.Trim());
				return resolved == null ? value : resolved.ToString();
			}, (e, v) => SetAttribute(e, attribute, v.ToJsString()));
		}

		private JsObject NewPrototype(JsObject parent, string name, bool constructible = false)
		{
			JsObject prototype = new JsObject(parent);
			prototype.Put(Interpreter.SymbolToStringTag, name, false);
			Interpreter.NewConstructor(name, 0, prototype, (i, t, a) => throw Interpreter.TypeError("Illegal constructor"), name == "HTMLElement" || name == "Element" ? (i, a, target) => ConstructCustomElement(target, prototype) : null);
			return prototype;
		}

		// new on an element interface, reached from a custom element class's super(): the element the registry names for that class
		private Element _constructing;

		private JsValue ConstructCustomElement(JsObject newTarget, JsObject fallback)
		{
			string tag = null;
			for (JsObject ctor = newTarget; ctor != null && tag == null; ctor = ctor.Prototype)
				_customElementTags.TryGetValue(ctor, out tag);
			if (tag == null && _constructing == null)
				throw Interpreter.TypeError("Illegal constructor");
			Element element = _constructing ?? new Element(tag);
			_constructing = null;
			JsNode wrapper = (JsNode)Wrap(element).Object;
			wrapper.Prototype = Interpreter.PrototypeFromConstructor(newTarget, fallback);
			return wrapper;
		}

		// Builds a registered custom element through its class, as createElement and the parser upgrade do
		internal JsValue ConstructRegistered(string tag, Element existing)
		{
			JsValue ctor = _customElements[tag];
			_constructing = existing;
			try
			{
				return Interpreter.Construct(ctor, Array.Empty<JsValue>(), ctor);
			}
			finally
			{
				_constructing = null;
			}
		}

		internal void CustomCallback(Element element, string name, params JsValue[] arguments)
		{
			if (_customElements.Count == 0 || !_customElements.ContainsKey(element.TagName) || !_wrappers.ContainsKey(element))
				return;
			JsValue callback = Interpreter.GetMember(Wrap(element), name);
			if (!callback.IsCallable)
				return;
			try
			{
				Interpreter.Call(callback, Wrap(element), arguments);
			}
			catch (JsThrow t)
			{
				Interpreter.ReportError(t.Value);
			}
		}

		private void ConnectCustomElements(Node node, string callback)
		{
			if (_customElements.Count == 0)
				return;
			if (node is Element element)
				CustomCallback(element, callback);
			foreach (Element descendant in node.DescendantElements())
				CustomCallback(descendant, callback);
		}

		private void SetupPrototypes()
		{
			EventTargetPrototype = NewPrototype(Interpreter.ObjectPrototype, "EventTarget");
			Interpreter.DefineMethod(EventTargetPrototype, "addEventListener", 2, (i, t, a) =>
			{
				AddListener(TargetKey(t), Interpreter.Arg(a, 0).ToJsString(), Interpreter.Arg(a, 1), Interpreter.Arg(a, 2));
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(EventTargetPrototype, "removeEventListener", 2, (i, t, a) =>
			{
				RemoveListener(TargetKey(t), Interpreter.Arg(a, 0).ToJsString(), Interpreter.Arg(a, 1), Interpreter.Arg(a, 2));
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(EventTargetPrototype, "dispatchEvent", 1, (i, t, a) =>
			{
				if (!(Interpreter.Arg(a, 0).IsObject && a[0].Object is JsEvent ev))
					throw Interpreter.TypeError("dispatchEvent: parameter 1 is not an Event");
				ev.Trusted = false;
				return DispatchEvent(ev, TargetKey(t));
			});
			JsFunction eventTargetConstructor = Interpreter.Global.Get("EventTarget", Interpreter).Function;
			eventTargetConstructor.NativeNew = (i, a, target) => new JsObject(Interpreter.PrototypeFromConstructor(target, EventTargetPrototype)) { Class = "EventTarget" };
			SetupEvents();
			SetupNode();
			SetupElement();
			SetupDocumentPrototype();
			SetupTextAndComment();
			SetupStyleBindings();
		}

		// The event target a listener call names; a bare addEventListener(...) in strict code has no this and means the window
		private object TargetKey(JsValue thisValue)
		{
			if (thisValue.IsObject && thisValue.Object is JsNode wrapper)
				return wrapper.Node;
			if (thisValue.IsObject)
				return thisValue.Object;
			if (thisValue.IsNullish)
				return Interpreter.Global;
			throw Interpreter.TypeError("Illegal invocation");
		}

		// Events

		private void SetupEvents()
		{
			EventPrototype = new JsObject(Interpreter.ObjectPrototype);
			EventPrototype.Put(Interpreter.SymbolToStringTag, "Event", false);
			JsFunction eventConstructor = Interpreter.NewConstructor("Event", 1, EventPrototype, null, (i, a, target) => ConstructEvent(a, target, EventPrototype));
			eventConstructor.Put("NONE", 0, false);
			eventConstructor.Put("CAPTURING_PHASE", 1, false);
			eventConstructor.Put("AT_TARGET", 2, false);
			eventConstructor.Put("BUBBLING_PHASE", 3, false);
			Interpreter.DefineGetter(EventPrototype, "type", (i, t, a) => ThisEvent(t).Type);
			Interpreter.DefineGetter(EventPrototype, "target", (i, t, a) => WrapTarget(ThisEvent(t).Target));
			Interpreter.DefineGetter(EventPrototype, "srcElement", (i, t, a) => WrapTarget(ThisEvent(t).Target));
			Interpreter.DefineGetter(EventPrototype, "currentTarget", (i, t, a) => ThisEvent(t).CurrentTarget == null ? JsValue.Null : WrapTarget(ThisEvent(t).CurrentTarget));
			Interpreter.DefineGetter(EventPrototype, "eventPhase", (i, t, a) => ThisEvent(t).Phase);
			Interpreter.DefineGetter(EventPrototype, "bubbles", (i, t, a) => ThisEvent(t).Bubbles);
			Interpreter.DefineGetter(EventPrototype, "cancelable", (i, t, a) => ThisEvent(t).Cancelable);
			Interpreter.DefineGetter(EventPrototype, "composed", (i, t, a) => ThisEvent(t).Composed);
			Interpreter.DefineGetter(EventPrototype, "defaultPrevented", (i, t, a) => ThisEvent(t).DefaultPrevented);
			Interpreter.DefineGetter(EventPrototype, "isTrusted", (i, t, a) => ThisEvent(t).Trusted);
			Interpreter.DefineGetter(EventPrototype, "timeStamp", (i, t, a) => ThisEvent(t).TimeStamp);
			Interpreter.DefineGetter(EventPrototype, "cancelBubble", (i, t, a) => ThisEvent(t).Stop, (i, t, a) =>
			{
				if (Interpreter.Arg(a, 0).Truthy)
					ThisEvent(t).Stop = true;
				return JsValue.Undefined;
			});
			Interpreter.DefineGetter(EventPrototype, "returnValue", (i, t, a) => !ThisEvent(t).DefaultPrevented, (i, t, a) =>
			{
				if (!Interpreter.Arg(a, 0).Truthy && ThisEvent(t).Cancelable)
					ThisEvent(t).DefaultPrevented = true;
				return JsValue.Undefined;
			});
			Interpreter.DefineGetter(EventPrototype, "detail", (i, t, a) => ThisEvent(t).Detail);
			Interpreter.DefineMethod(EventPrototype, "preventDefault", 0, (i, t, a) =>
			{
				JsEvent ev = ThisEvent(t);
				if (ev.Cancelable)
					ev.DefaultPrevented = true;
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(EventPrototype, "stopPropagation", 0, (i, t, a) =>
			{
				ThisEvent(t).Stop = true;
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(EventPrototype, "stopImmediatePropagation", 0, (i, t, a) =>
			{
				JsEvent ev = ThisEvent(t);
				ev.Stop = true;
				ev.StopImmediate = true;
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(EventPrototype, "composedPath", 0, (i, t, a) =>
			{
				JsArray path = Interpreter.NewArray();
				if (ThisEvent(t).Target is Node node)
				{
					for (Node n = node; n != null; n = n.Parent)
						path.Elements.Add(Wrap(n));
					path.Elements.Add(Interpreter.Global);
				}
				else if (ThisEvent(t).Target is JsObject obj)
					path.Elements.Add(obj);
				return path;
			});
			Interpreter.DefineMethod(EventPrototype, "initEvent", 3, (i, t, a) =>
			{
				JsEvent ev = ThisEvent(t);
				ev.Type = Interpreter.Arg(a, 0).ToJsString();
				ev.Bubbles = Interpreter.Arg(a, 1).Truthy;
				ev.Cancelable = Interpreter.Arg(a, 2).Truthy;
				return JsValue.Undefined;
			});
			CustomEventPrototype = new JsObject(EventPrototype);
			CustomEventPrototype.Put(Interpreter.SymbolToStringTag, "CustomEvent", false);
			Interpreter.NewConstructor("CustomEvent", 1, CustomEventPrototype, null, (i, a, target) =>
			{
				JsEvent ev = ConstructEvent(a, target, CustomEventPrototype);
				JsValue init = Interpreter.Arg(a, 1);
				if (init.IsObject)
					ev.Detail = Interpreter.GetMember(init, "detail");
				return ev;
			});
			Interpreter.DefineMethod(CustomEventPrototype, "initCustomEvent", 4, (i, t, a) =>
			{
				JsEvent ev = ThisEvent(t);
				ev.Type = Interpreter.Arg(a, 0).ToJsString();
				ev.Bubbles = Interpreter.Arg(a, 1).Truthy;
				ev.Cancelable = Interpreter.Arg(a, 2).Truthy;
				ev.Detail = Interpreter.Arg(a, 3);
				return JsValue.Undefined;
			});
			JsObject uiEvent = new JsObject(EventPrototype);
			uiEvent.Put(Interpreter.SymbolToStringTag, "UIEvent", false);
			Interpreter.NewConstructor("UIEvent", 1, uiEvent, null, (i, a, target) => ConstructEvent(a, target, uiEvent));
			MouseEventPrototype = EventSubtype("MouseEvent", uiEvent, new[] { "clientX", "clientY", "pageX", "pageY", "screenX", "screenY", "offsetX", "offsetY", "button", "buttons", "detail", "shiftKey", "ctrlKey", "altKey", "metaKey", "relatedTarget", "movementX", "movementY" });
			EventSubtype("PointerEvent", MouseEventPrototype, new[] { "pointerId", "pointerType", "isPrimary", "width", "height", "pressure" });
			EventSubtype("WheelEvent", MouseEventPrototype, new[] { "deltaX", "deltaY", "deltaZ", "deltaMode" });
			KeyboardEventPrototype = EventSubtype("KeyboardEvent", uiEvent, new[] { "key", "code", "keyCode", "which", "charCode", "shiftKey", "ctrlKey", "altKey", "metaKey", "repeat", "isComposing", "location" });
			InputEventPrototype = EventSubtype("InputEvent", uiEvent, new[] { "data", "inputType", "isComposing" });
			EventSubtype("FocusEvent", uiEvent, new[] { "relatedTarget" });
			EventSubtype("TouchEvent", uiEvent, new[] { "touches", "targetTouches", "changedTouches", "shiftKey", "ctrlKey", "altKey", "metaKey" });
			EventSubtype("ErrorEvent", EventPrototype, new[] { "message", "filename", "lineno", "colno", "error" });
			EventSubtype("MessageEvent", EventPrototype, new[] { "data", "origin", "lastEventId", "source", "ports" });
			EventSubtype("PopStateEvent", EventPrototype, new[] { "state" });
			EventSubtype("HashChangeEvent", EventPrototype, new[] { "oldURL", "newURL" });
			EventSubtype("StorageEvent", EventPrototype, new[] { "key", "oldValue", "newValue", "url", "storageArea" });
			EventSubtype("PromiseRejectionEvent", EventPrototype, new[] { "promise", "reason" });
			EventSubtype("TransitionEvent", EventPrototype, new[] { "propertyName", "elapsedTime", "pseudoElement" });
			EventSubtype("AnimationEvent", EventPrototype, new[] { "animationName", "elapsedTime", "pseudoElement" });
			EventSubtype("ProgressEvent", EventPrototype, new[] { "lengthComputable", "loaded", "total" });
			EventSubtype("SubmitEvent", EventPrototype, new[] { "submitter" });
			EventSubtype("CompositionEvent", uiEvent, new[] { "data" });
			EventSubtype("DragEvent", MouseEventPrototype, new[] { "dataTransfer" });
			EventSubtype("BeforeUnloadEvent", EventPrototype, Array.Empty<string>());
			EventSubtype("PageTransitionEvent", EventPrototype, new[] { "persisted" });
			EventSubtype("ClipboardEvent", EventPrototype, new[] { "clipboardData" });
		}

		private JsObject EventSubtype(string name, JsObject parent, string[] fields)
		{
			JsObject prototype = new JsObject(parent);
			prototype.Put(Interpreter.SymbolToStringTag, name, false);
			Interpreter.NewConstructor(name, 1, prototype, null, (i, a, target) =>
			{
				JsEvent ev = ConstructEvent(a, target, prototype);
				JsValue init = Interpreter.Arg(a, 1);
				foreach (string field in fields)
				{
					JsValue value = init.IsObject ? Interpreter.GetMember(init, field) : JsValue.Undefined;
					if (value.IsUndefined)
						value = field is "shiftKey" or "ctrlKey" or "altKey" or "metaKey" or "repeat" or "isComposing" or "lengthComputable" or "persisted" ? JsValue.False : field is "relatedTarget" or "data" or "error" or "state" or "source" or "submitter" or "dataTransfer" or "clipboardData" ? JsValue.Null : field is "key" or "code" or "inputType" or "message" or "filename" or "origin" or "lastEventId" or "propertyName" or "animationName" or "pseudoElement" or "pointerType" or "oldURL" or "newURL" ? JsValue.EmptyString : 0;
					ev.Put(field, value);
				}
				if (init.IsObject && !Interpreter.GetMember(init, "detail").IsUndefined)
					ev.Detail = Interpreter.GetMember(init, "detail");
				return ev;
			});
			return prototype;
		}

		private JsEvent ConstructEvent(JsValue[] arguments, JsObject newTarget, JsObject fallback)
		{
			if (arguments.Length == 0)
				throw Interpreter.TypeError("Failed to construct 'Event': 1 argument required, but only 0 present.");
			JsEvent ev = new JsEvent(Interpreter.PrototypeFromConstructor(newTarget, fallback)) { Type = arguments[0].ToJsString(), TimeStamp = Now };
			JsValue init = Interpreter.Arg(arguments, 1);
			if (init.IsObject)
			{
				ev.Bubbles = Interpreter.GetMember(init, "bubbles").Truthy;
				ev.Cancelable = Interpreter.GetMember(init, "cancelable").Truthy;
				ev.Composed = Interpreter.GetMember(init, "composed").Truthy;
			}
			return ev;
		}

		private JsEvent ThisEvent(JsValue thisValue)
		{
			if (thisValue.IsObject && thisValue.Object is JsEvent ev)
				return ev;
			throw Interpreter.TypeError("Illegal invocation");
		}

		// Node

		private void SetupNode()
		{
			NodePrototype = NewPrototype(EventTargetPrototype, "Node");
			JsFunction nodeConstructor = Interpreter.Global.Get("Node", Interpreter).Function;
			nodeConstructor.Put("ELEMENT_NODE", 1, false);
			nodeConstructor.Put("ATTRIBUTE_NODE", 2, false);
			nodeConstructor.Put("TEXT_NODE", 3, false);
			nodeConstructor.Put("COMMENT_NODE", 8, false);
			nodeConstructor.Put("DOCUMENT_NODE", 9, false);
			nodeConstructor.Put("DOCUMENT_FRAGMENT_NODE", 11, false);
			nodeConstructor.Put("DOCUMENT_POSITION_PRECEDING", 2, false);
			nodeConstructor.Put("DOCUMENT_POSITION_FOLLOWING", 4, false);
			nodeConstructor.Put("DOCUMENT_POSITION_CONTAINS", 8, false);
			nodeConstructor.Put("DOCUMENT_POSITION_CONTAINED_BY", 16, false);
			foreach (string key in new[] { "ELEMENT_NODE", "TEXT_NODE", "COMMENT_NODE", "DOCUMENT_NODE", "DOCUMENT_FRAGMENT_NODE" })
				NodePrototype.Put(key, nodeConstructor.Get(key, Interpreter), false);
			Getter(NodePrototype, "nodeType", n => NodeTypeOf(n));
			Getter(NodePrototype, "nodeName", n => NodeNameOf(n));
			Getter(NodePrototype, "nodeValue", n => n is TextNode text ? text.Data : n is Comment comment ? comment.Data : JsValue.Null, (n, v) =>
			{
				if (n is TextNode text)
				{
					text.Data = v.ToJsString();
					Mutated(n, "characterData");
				}
			});
			Getter(NodePrototype, "textContent", n => n is Document ? JsValue.Null : n.TextContent, (n, v) => SetTextContent(n, v.IsNullish ? "" : v.ToJsString()));
			Getter(NodePrototype, "parentNode", n => Wrap(n.Parent));
			Getter(NodePrototype, "parentElement", n => Wrap(n.Parent is Element ? n.Parent : null));
			Getter(NodePrototype, "childNodes", n => WrapList(n.Children));
			Getter(NodePrototype, "firstChild", n => Wrap(n.FirstChild));
			Getter(NodePrototype, "lastChild", n => Wrap(n.LastChild));
			Getter(NodePrototype, "nextSibling", n => Wrap(Sibling(n, 1)));
			Getter(NodePrototype, "previousSibling", n => Wrap(Sibling(n, -1)));
			Getter(NodePrototype, "ownerDocument", n => n is Document ? JsValue.Null : Wrap(Document));
			Getter(NodePrototype, "isConnected", n => IsConnected(n));
			Getter(NodePrototype, "baseURI", n => Url?.ToString() ?? "");
			Interpreter.DefineMethod(NodePrototype, "appendChild", 1, (i, t, a) => Insert(ThisNode(t), RequireNode(Interpreter.Arg(a, 0)), null));
			Interpreter.DefineMethod(NodePrototype, "insertBefore", 2, (i, t, a) => Insert(ThisNode(t), RequireNode(Interpreter.Arg(a, 0)), Unwrap(Interpreter.Arg(a, 1))));
			Interpreter.DefineMethod(NodePrototype, "removeChild", 1, (i, t, a) =>
			{
				Node parent = ThisNode(t);
				Node child = RequireNode(Interpreter.Arg(a, 0));
				if (child.Parent != parent)
					throw DomException("NotFoundError", "The node to be removed is not a child of this node.");
				Remove(child);
				return Wrap(child);
			});
			Interpreter.DefineMethod(NodePrototype, "replaceChild", 2, (i, t, a) =>
			{
				Node parent = ThisNode(t);
				Node replacement = RequireNode(Interpreter.Arg(a, 0));
				Node old = RequireNode(Interpreter.Arg(a, 1));
				if (old.Parent != parent)
					throw DomException("NotFoundError", "The node to be replaced is not a child of this node.");
				Node after = Sibling(old, 1);
				Remove(old);
				Insert(parent, replacement, after);
				return Wrap(old);
			});
			Interpreter.DefineMethod(NodePrototype, "cloneNode", 1, (i, t, a) => Wrap(Clone(ThisNode(t), Interpreter.Arg(a, 0).Truthy)));
			Interpreter.DefineMethod(NodePrototype, "contains", 1, (i, t, a) =>
			{
				Node self = ThisNode(t);
				for (Node n = Unwrap(Interpreter.Arg(a, 0)); n != null; n = n.Parent)
				{
					if (n == self)
						return true;
				}
				return false;
			});
			Interpreter.DefineMethod(NodePrototype, "hasChildNodes", 0, (i, t, a) => ThisNode(t).Children.Count > 0);
			Interpreter.DefineMethod(NodePrototype, "normalize", 0, (i, t, a) => JsValue.Undefined);
			Interpreter.DefineMethod(NodePrototype, "getRootNode", 0, (i, t, a) =>
			{
				bool composed = Interpreter.Arg(a, 0).IsObject && Interpreter.GetMember(a[0], "composed").Truthy;
				Node n = ThisNode(t);
				while (n.Parent != null && (composed || !(n is Element { TagName: Element.ShadowRootTag })))
					n = n.Parent;
				return Wrap(n);
			});
			Interpreter.DefineMethod(NodePrototype, "isEqualNode", 1, (i, t, a) => Unwrap(Interpreter.Arg(a, 0)) is Node other && other.OuterHtml == ThisNode(t).OuterHtml);
			Interpreter.DefineMethod(NodePrototype, "isSameNode", 1, (i, t, a) => Unwrap(Interpreter.Arg(a, 0)) == ThisNode(t));
			Interpreter.DefineMethod(NodePrototype, "compareDocumentPosition", 1, (i, t, a) =>
			{
				Node self = ThisNode(t);
				Node other = Unwrap(Interpreter.Arg(a, 0));
				if (other == null || other == self)
					return 0;
				for (Node n = other.Parent; n != null; n = n.Parent)
				{
					if (n == self)
						return 20;
				}
				for (Node n = self.Parent; n != null; n = n.Parent)
				{
					if (n == other)
						return 10;
				}
				int selfIndex = -1;
				int otherIndex = -1;
				int index = 0;
				foreach (Node n in Document.Descendants())
				{
					if (n == self)
						selfIndex = index;
					if (n == other)
						otherIndex = index;
					index++;
				}
				if (selfIndex < 0 || otherIndex < 0)
					return 35;
				return otherIndex < selfIndex ? 2 : 4;
			});
			foreach (string name in HandlerNames)
			{
				string type = name;
				Interpreter.DefineGetter(NodePrototype, "on" + type, (i, t, a) => HandlerFor(TargetKey(t), type), (i, t, a) =>
				{
					SetHandler(TargetKey(t), type, Interpreter.Arg(a, 0));
					return JsValue.Undefined;
				});
			}
		}

		private static int NodeTypeOf(Node node) => node switch
		{
			Html.Document => 9,
			Element { TagName: FragmentTag or Element.ShadowRootTag } => 11,
			Element => 1,
			TextNode => 3,
			_ => 8,
		};

		private static string NodeNameOf(Node node) => node switch
		{
			Html.Document => "#document",
			Element { TagName: FragmentTag or Element.ShadowRootTag } => "#document-fragment",
			Element element => element.TagName.ToUpperInvariant(),
			TextNode => "#text",
			_ => "#comment",
		};

		private static Node Sibling(Node node, int step)
		{
			if (node.Parent == null)
				return null;
			int position = node.Parent.Children.IndexOf(node);
			if (position < 0)
				return null;
			int index = position + step;
			return index >= 0 && index < node.Parent.Children.Count ? node.Parent.Children[index] : null;
		}

		private static Element ElementSibling(Node node, int step)
		{
			for (Node n = Sibling(node, step); n != null; n = Sibling(n, step))
			{
				if (n is Element element)
					return element;
			}
			return null;
		}

		private Node RequireNode(JsValue value)
		{
			Node node = Unwrap(value);
			if (node == null)
				throw Interpreter.TypeError("parameter 1 is not of type 'Node'.");
			return node;
		}

		internal JsThrow DomException(string name, string message)
		{
			JsObject error = Interpreter.NewError("Error", message);
			error.Put("name", name, false);
			error.Put("code", name == "NotFoundError" ? 8 : name == "HierarchyRequestError" ? 3 : name == "SyntaxError" ? 12 : 0, false);
			if (Interpreter.Global.Get("DOMException", Interpreter) is { IsObject: true } ctor)
			{
				JsValue prototype = Interpreter.GetMember(ctor, "prototype");
				if (prototype.IsObject)
					error.Prototype = prototype.Object;
			}
			return new JsThrow(error);
		}

		// Inserts a node (or a fragment's children) before a reference child, running any scripts that arrive in the document
		internal JsValue Insert(Node parent, Node child, Node reference)
		{
			if (parent is TextNode || parent is Comment)
				throw DomException("HierarchyRequestError", "This node type does not support children.");
			if (child is Document)
				throw DomException("HierarchyRequestError", "A document cannot be inserted.");
			for (Node n = parent; n != null; n = n.Parent)
			{
				if (n == child)
					throw DomException("HierarchyRequestError", "The new child element contains the parent.");
			}
			if (reference != null && reference.Parent != parent)
				throw DomException("NotFoundError", "The node before which the new node is to be inserted is not a child of this node.");
			List<Node> added = new List<Node>();
			if (child is Element { TagName: FragmentTag } fragment)
			{
				added.AddRange(fragment.Children);
				foreach (Node item in added)
				{
					Node oldParent = item.Parent;
					parent.InsertBefore(item, reference);
					if (oldParent != null && oldParent != fragment && oldParent != parent)
						Mutated(oldParent, "childList", null, null, new List<Node> { item });
				}
				fragment.Children.Clear();
			}
			else
			{
				if (reference == child)
					return Wrap(child);
				Node oldParent = child.Parent;
				parent.InsertBefore(child, reference);
				if (oldParent != null && oldParent != parent)
					Mutated(oldParent, "childList", null, null, new List<Node> { child });
				added.Add(child);
			}
			Mutated(parent, "childList", null, added, null);
			foreach (Node item in added)
			{
				RunInsertedScripts(item);
				if (IsConnected(item))
					ConnectCustomElements(item, "connectedCallback");
			}
			return Wrap(child);
		}

		internal void Remove(Node child)
		{
			Node parent = child.Parent;
			if (parent == null)
				return;
			bool connected = IsConnected(child);
			parent.RemoveChild(child);
			Mutated(parent, "childList", null, null, new List<Node> { child });
			if (connected)
				ConnectCustomElements(child, "disconnectedCallback");
		}

		private Node Clone(Node node, bool deep)
		{
			Node copy;
			switch (node)
			{
				case Element element:
				{
					Element clone = new Element(element.TagName);
					foreach (KeyValuePair<string, string> attribute in element.Attributes)
						clone.Attributes.Add(attribute);
					copy = clone;
					break;
				}
				case TextNode text:
					copy = new TextNode(text.Data);
					break;
				case Comment comment:
					copy = new Comment(comment.Data);
					break;
				default:
					copy = new Document();
					break;
			}
			if (deep)
			{
				foreach (Node child in node.Children)
					copy.AppendChild(Clone(child, true));
			}
			return copy;
		}

		private void SetTextContent(Node node, string text)
		{
			if (node is TextNode textNode)
			{
				textNode.Data = text;
				Mutated(node, "characterData");
				return;
			}
			if (node is Comment)
				return;
			List<Node> removed = new List<Node>(node.Children);
			foreach (Node child in removed)
				node.RemoveChild(child);
			List<Node> added = null;
			if (text.Length > 0)
			{
				TextNode fresh = new TextNode(text);
				node.AppendChild(fresh);
				added = new List<Node> { fresh };
			}
			Mutated(node, "childList", null, added, removed);
		}

		// Element

		private void SetupElement()
		{
			ElementPrototype = NewPrototype(NodePrototype, "Element");
			HtmlElementPrototype = NewPrototype(ElementPrototype, "HTMLElement");
			_tagPrototypes["HTMLElement"] = HtmlElementPrototype;
			foreach (string interfaceName in InterfaceNames)
				PrototypeForTag(null, interfaceName);
			JsObject proto = ElementPrototype;
			ElementGetter(proto, "tagName", e => e.TagName.ToUpperInvariant());
			ElementGetter(proto, "localName", e => e.TagName);
			ElementGetter(proto, "namespaceURI", e => InterfaceFor(e.TagName) == "SVGElement" ? "http://www.w3.org/2000/svg" : "http://www.w3.org/1999/xhtml");
			ElementGetter(proto, "prefix", e => JsValue.Null);
			Attribute(proto, "id", "id");
			Attribute(proto, "className", "class");
			Attribute(proto, "slot", "slot");
			ElementGetter(proto, "classList", e => TokenList(e, "class"));
			ElementGetter(proto, "attributes", e =>
			{
				JsArray list = Interpreter.NewArray();
				foreach (KeyValuePair<string, string> attribute in e.Attributes)
				{
					JsObject attr = Interpreter.NewObject();
					attr.Class = "Attr";
					attr.Put("name", attribute.Key);
					attr.Put("localName", attribute.Key);
					attr.Put("value", attribute.Value);
					attr.Put("nodeName", attribute.Key);
					attr.Put("nodeValue", attribute.Value);
					attr.Put("specified", true);
					list.Elements.Add(attr);
				}
				Interpreter.DefineMethod(list, "getNamedItem", 1, (i, t, a) =>
				{
					string name = Interpreter.Arg(a, 0).ToJsString().ToLowerInvariant();
					foreach (JsValue item in list.Elements)
					{
						if (Interpreter.GetMember(item, "name").ToJsString() == name)
							return item;
					}
					return JsValue.Null;
				});
				Interpreter.DefineMethod(list, "item", 1, (i, t, a) =>
				{
					int index = Interpreter.Arg(a, 0).ToInt32();
					return index >= 0 && index < list.Elements.Count ? list.Elements[index] : JsValue.Null;
				});
				return list;
			});
			Interpreter.DefineMethod(proto, "getAttribute", 1, (i, t, a) =>
			{
				string value = ThisElement(t).GetAttribute(Interpreter.Arg(a, 0).ToJsString().ToLowerInvariant());
				return value == null ? JsValue.Null : value;
			});
			Interpreter.DefineMethod(proto, "getAttributeNS", 2, (i, t, a) =>
			{
				string value = ThisElement(t).GetAttribute(Interpreter.Arg(a, 1).ToJsString().ToLowerInvariant());
				return value == null ? JsValue.Null : value;
			});
			Interpreter.DefineMethod(proto, "setAttribute", 2, (i, t, a) =>
			{
				SetAttribute(ThisElement(t), Interpreter.Arg(a, 0).ToJsString().ToLowerInvariant(), Interpreter.Arg(a, 1).ToJsString());
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(proto, "setAttributeNS", 3, (i, t, a) =>
			{
				SetAttribute(ThisElement(t), Interpreter.Arg(a, 1).ToJsString().ToLowerInvariant(), Interpreter.Arg(a, 2).ToJsString());
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(proto, "removeAttribute", 1, (i, t, a) =>
			{
				RemoveAttribute(ThisElement(t), Interpreter.Arg(a, 0).ToJsString().ToLowerInvariant());
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(proto, "hasAttribute", 1, (i, t, a) => ThisElement(t).HasAttribute(Interpreter.Arg(a, 0).ToJsString().ToLowerInvariant()));
			Interpreter.DefineMethod(proto, "hasAttributes", 0, (i, t, a) => ThisElement(t).Attributes.Count > 0);
			Interpreter.DefineMethod(proto, "getAttributeNames", 0, (i, t, a) =>
			{
				List<string> names = new List<string>();
				foreach (KeyValuePair<string, string> attribute in ThisElement(t).Attributes)
					names.Add(attribute.Key);
				return Interpreter.NewStringArray(names);
			});
			Interpreter.DefineMethod(proto, "toggleAttribute", 1, (i, t, a) =>
			{
				Element element = ThisElement(t);
				string name = Interpreter.Arg(a, 0).ToJsString().ToLowerInvariant();
				bool force = a.Length > 1 ? a[1].Truthy : !element.HasAttribute(name);
				if (force)
					SetAttribute(element, name, element.GetAttribute(name) ?? "");
				else
					RemoveAttribute(element, name);
				return force;
			});
			ElementGetter(proto, "innerHTML", e => e.TagName == FragmentTag ? e.InnerHtml : e.InnerHtml, (e, v) => SetInnerHtml(e, v.IsNullish ? "" : v.ToJsString()));
			ElementGetter(proto, "outerHTML", e => e.OuterHtml, (e, v) =>
			{
				if (e.Parent == null)
					return;
				Element fragment = ParseFragment(v.ToJsString(), e.Parent as Element);
				Node after = Sibling(e, 1);
				Node parent = e.Parent;
				Remove(e);
				Insert(parent, fragment, after);
			});
			ElementGetter(proto, "children", e => ElementList(e.Children));
			ElementGetter(proto, "childElementCount", e =>
			{
				int count = 0;
				foreach (Node child in e.Children)
					count += child is Element ? 1 : 0;
				return count;
			});
			ElementGetter(proto, "firstElementChild", e => Wrap(FirstElementChild(e, false)));
			ElementGetter(proto, "lastElementChild", e => Wrap(FirstElementChild(e, true)));
			ElementGetter(proto, "nextElementSibling", e => Wrap(ElementSibling(e, 1)));
			ElementGetter(proto, "previousElementSibling", e => Wrap(ElementSibling(e, -1)));
			Interpreter.DefineMethod(proto, "querySelector", 1, (i, t, a) => Wrap(QueryFirst(ThisNode(t), Interpreter.Arg(a, 0).ToJsString())));
			Interpreter.DefineMethod(proto, "querySelectorAll", 1, (i, t, a) => QueryAll(ThisNode(t), Interpreter.Arg(a, 0).ToJsString()));
			Interpreter.DefineMethod(proto, "getElementsByTagName", 1, (i, t, a) => ByTag(ThisNode(t), Interpreter.Arg(a, 0).ToJsString()));
			Interpreter.DefineMethod(proto, "getElementsByTagNameNS", 2, (i, t, a) => ByTag(ThisNode(t), Interpreter.Arg(a, 1).ToJsString()));
			Interpreter.DefineMethod(proto, "getElementsByClassName", 1, (i, t, a) => ByClass(ThisNode(t), Interpreter.Arg(a, 0).ToJsString()));
			Interpreter.DefineMethod(proto, "matches", 1, (i, t, a) => Matches(ThisElement(t), Interpreter.Arg(a, 0).ToJsString()));
			proto.Put("webkitMatchesSelector", proto.Get("matches", Interpreter), false);
			proto.Put("msMatchesSelector", proto.Get("matches", Interpreter), false);
			Interpreter.DefineMethod(proto, "closest", 1, (i, t, a) =>
			{
				List<Selector> selectors = ParseSelectors(Interpreter.Arg(a, 0).ToJsString());
				for (Node n = ThisElement(t); n is Element element; n = n.Parent)
				{
					if (MatchesAny(element, selectors))
						return Wrap(element);
				}
				return JsValue.Null;
			});
			Interpreter.DefineMethod(proto, "append", 1, (i, t, a) =>
			{
				Node parent = ThisNode(t);
				foreach (JsValue item in a)
					Insert(parent, NodeOrText(item), null);
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(proto, "prepend", 1, (i, t, a) =>
			{
				Node parent = ThisNode(t);
				Node first = parent.FirstChild;
				foreach (JsValue item in a)
					Insert(parent, NodeOrText(item), first);
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(proto, "before", 1, (i, t, a) =>
			{
				Node self = ThisNode(t);
				if (self.Parent == null)
					return JsValue.Undefined;
				foreach (JsValue item in a)
					Insert(self.Parent, NodeOrText(item), self);
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(proto, "after", 1, (i, t, a) =>
			{
				Node self = ThisNode(t);
				if (self.Parent == null)
					return JsValue.Undefined;
				Node reference = Sibling(self, 1);
				foreach (JsValue item in a)
					Insert(self.Parent, NodeOrText(item), reference);
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(proto, "replaceWith", 1, (i, t, a) =>
			{
				Node self = ThisNode(t);
				if (self.Parent == null)
					return JsValue.Undefined;
				Node parent = self.Parent;
				Node reference = Sibling(self, 1);
				Remove(self);
				foreach (JsValue item in a)
					Insert(parent, NodeOrText(item), reference);
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(proto, "replaceChildren", 0, (i, t, a) =>
			{
				Node self = ThisNode(t);
				SetTextContent(self, "");
				foreach (JsValue item in a)
					Insert(self, NodeOrText(item), null);
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(proto, "remove", 0, (i, t, a) =>
			{
				Remove(ThisNode(t));
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(proto, "insertAdjacentHTML", 2, (i, t, a) =>
			{
				Element element = ThisElement(t);
				string where = Interpreter.Arg(a, 0).ToJsString().ToLowerInvariant();
				Element fragment = ParseFragment(Interpreter.Arg(a, 1).ToJsString(), where is "beforebegin" or "afterend" ? element.Parent as Element : element);
				InsertAdjacent(element, where, fragment);
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(proto, "insertAdjacentElement", 2, (i, t, a) =>
			{
				Node node = RequireNode(Interpreter.Arg(a, 1));
				InsertAdjacent(ThisElement(t), Interpreter.Arg(a, 0).ToJsString().ToLowerInvariant(), node);
				return Wrap(node);
			});
			Interpreter.DefineMethod(proto, "insertAdjacentText", 2, (i, t, a) =>
			{
				InsertAdjacent(ThisElement(t), Interpreter.Arg(a, 0).ToJsString().ToLowerInvariant(), new TextNode(Interpreter.Arg(a, 1).ToJsString()));
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(proto, "getBoundingClientRect", 0, (i, t, a) => BoundingRect(ThisElement(t)));
			Interpreter.DefineMethod(proto, "getClientRects", 0, (i, t, a) => Interpreter.NewArray(new List<JsValue> { BoundingRect(ThisElement(t)) }));
			ElementGetter(proto, "clientWidth", e => Round(LayoutBoxOf(e)?.ContentWidth + LayoutBoxOf(e)?.PaddingLeft + LayoutBoxOf(e)?.PaddingRight ?? (e == Document.DocumentElement ? ViewportWidth : 0)));
			ElementGetter(proto, "clientHeight", e => Round(LayoutBoxOf(e)?.ContentHeight + LayoutBoxOf(e)?.PaddingTop + LayoutBoxOf(e)?.PaddingBottom ?? (e == Document.DocumentElement ? ViewportHeight : 0)));
			ElementGetter(proto, "clientTop", e => Round(LayoutBoxOf(e)?.BorderTop ?? 0));
			ElementGetter(proto, "clientLeft", e => Round(LayoutBoxOf(e)?.BorderLeft ?? 0));
			ElementGetter(proto, "scrollWidth", e => Round(LayoutBoxOf(e)?.Width ?? 0));
			ElementGetter(proto, "scrollHeight", e => Round(LayoutBoxOf(e)?.Height ?? 0));
			ElementGetter(proto, "scrollTop", e => e == Document.DocumentElement || e == Document.Body ? ScrollY?.Invoke() ?? 0 : 0, (e, v) =>
			{
				if (e == Document.DocumentElement || e == Document.Body)
					ScrollTo?.Invoke(v.ToInt32());
			});
			ElementGetter(proto, "scrollLeft", e => 0, (e, v) => { });
			Interpreter.DefineMethod(proto, "scrollIntoView", 0, (i, t, a) =>
			{
				LayoutBox box = LayoutBoxOf(ThisElement(t));
				if (box != null)
					ScrollTo?.Invoke((int)box.Y);
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(proto, "scrollTo", 0, (i, t, a) => JsValue.Undefined);
			Interpreter.DefineMethod(proto, "scroll", 0, (i, t, a) => JsValue.Undefined);
			Interpreter.DefineMethod(proto, "scrollBy", 0, (i, t, a) => JsValue.Undefined);
			Interpreter.DefineMethod(proto, "attachShadow", 1, (i, t, a) =>
			{
				Element host = ThisElement(t);
				if (host.ShadowRoot != null)
					throw DomException("NotSupportedError", "Shadow root cannot be created on a host which already hosts a shadow tree.");
				JsValue mode = Interpreter.Arg(a, 0).IsObject ? Interpreter.GetMember(a[0], "mode") : JsValue.Undefined;
				Element shadow = host.AttachShadow();
				JsValue wrapper = Wrap(shadow);
				wrapper.Object.Put("mode", mode.IsUndefined ? "open" : mode.ToJsString(), false);
				Mutated(host, "childList");
				return wrapper;
			});
			ElementGetter(proto, "shadowRoot", e => Wrap(e.ShadowRoot));
			ElementGetter(proto, "assignedSlot", e =>
			{
				Element host = e.Parent as Element;
				if (host?.ShadowRoot == null)
					return JsValue.Null;
				string name = e.GetAttribute("slot") ?? "";
				foreach (Element slot in host.ShadowRoot.DescendantElements())
				{
					if (slot.TagName == "slot" && (slot.GetAttribute("name") ?? "") == name)
						return Wrap(slot);
				}
				return JsValue.Null;
			});
			Interpreter.DefineMethod(proto, "assignedNodes", 0, (i, t, a) =>
			{
				Element slot = ThisElement(t);
				Element host = slot.ShadowHost;
				return host == null ? Interpreter.NewArray() : WrapList(host.AssignedTo(slot.GetAttribute("name")));
			});
			Interpreter.DefineMethod(proto, "assignedElements", 0, (i, t, a) =>
			{
				Element slot = ThisElement(t);
				Element host = slot.ShadowHost;
				return host == null ? Interpreter.NewArray() : ElementList(host.AssignedTo(slot.GetAttribute("name")));
			});
			Interpreter.DefineMethod(proto, "animate", 2, (i, t, a) =>
			{
				JsObject animation = Interpreter.NewObject();
				animation.Put("finished", Interpreter.PromiseResolve(JsValue.Undefined));
				animation.Put("playState", "finished");
				Interpreter.DefineMethod(animation, "cancel", 0, (ii, tt, aa) => JsValue.Undefined);
				Interpreter.DefineMethod(animation, "finish", 0, (ii, tt, aa) => JsValue.Undefined);
				Interpreter.DefineMethod(animation, "play", 0, (ii, tt, aa) => JsValue.Undefined);
				Interpreter.DefineMethod(animation, "pause", 0, (ii, tt, aa) => JsValue.Undefined);
				Interpreter.DefineMethod(animation, "addEventListener", 2, (ii, tt, aa) => JsValue.Undefined);
				return animation;
			});
			Interpreter.DefineMethod(proto, "getAnimations", 0, (i, t, a) => Interpreter.NewArray());
			Interpreter.DefineMethod(proto, "requestFullscreen", 0, (i, t, a) => Interpreter.PromiseResolve(JsValue.Undefined));
			Interpreter.DefineMethod(proto, "setPointerCapture", 1, (i, t, a) => JsValue.Undefined);
			Interpreter.DefineMethod(proto, "releasePointerCapture", 1, (i, t, a) => JsValue.Undefined);
			Interpreter.DefineMethod(proto, "hasPointerCapture", 1, (i, t, a) => false);
			ElementGetter(proto, "part", e => TokenList(e, "part"));
			SetupHtmlElement();
		}

		private static double Round(float? value) => Math.Round(value ?? 0);

		private static Element FirstElementChild(Node node, bool last)
		{
			for (int i = 0; i < node.Children.Count; i++)
			{
				Node child = node.Children[last ? node.Children.Count - 1 - i : i];
				if (child is Element element)
					return element;
			}
			return null;
		}

		private Node NodeOrText(JsValue value) => Unwrap(value) ?? new TextNode(value.ToJsString());

		private void InsertAdjacent(Element element, string where, Node node)
		{
			switch (where)
			{
				case "beforebegin":
					if (element.Parent != null)
						Insert(element.Parent, node, element);
					break;
				case "afterbegin":
					Insert(element, node, element.FirstChild);
					break;
				case "beforeend":
					Insert(element, node, null);
					break;
				case "afterend":
					if (element.Parent != null)
						Insert(element.Parent, node, Sibling(element, 1));
					break;
				default:
					throw DomException("SyntaxError", "The value provided ('" + where + "') is not one of 'beforeBegin', 'afterBegin', 'beforeEnd', or 'afterEnd'.");
			}
		}

		internal void SetAttribute(Element element, string name, string value)
		{
			string old = element.GetAttribute(name);
			if (old == value)
				return;
			element.SetAttribute(name, value);
			Mutated(element, "attributes", name);
			AttributeChanged(element, name, old, value);
		}

		internal void RemoveAttribute(Element element, string name)
		{
			string old = element.GetAttribute(name);
			if (old == null)
				return;
			element.RemoveAttribute(name);
			Mutated(element, "attributes", name);
			AttributeChanged(element, name, old, null);
		}

		private void AttributeChanged(Element element, string name, string old, string value)
		{
			if (_customElements.Count == 0 || !_customElements.TryGetValue(element.TagName, out JsValue ctor))
				return;
			JsValue observed = Interpreter.GetMember(ctor, "observedAttributes");
			if (!observed.IsObject)
				return;
			foreach (JsValue attribute in Interpreter.Iterate(observed))
			{
				if (attribute.ToJsString() == name)
				{
					CustomCallback(element, "attributeChangedCallback", name, old == null ? JsValue.Null : old, value == null ? JsValue.Null : value);
					return;
				}
			}
		}

		// Markup to nodes: parsed as a page, then the pieces that would land inside the context element are lifted out
		internal Element ParseFragment(string html, Element context)
		{
			Element fragment = new Element(FragmentTag);
			string tag = context?.TagName ?? "body";
			if (tag == "script" || tag == "style" || tag == "textarea" || tag == "title")
			{
				fragment.AppendChild(new TextNode(html));
				return fragment;
			}
			string prefix = "";
			string[] path;
			switch (tag)
			{
				case "tr":
					prefix = "<table><tbody><tr>";
					path = new[] { "table", "tbody", "tr" };
					break;
				case "td":
				case "th":
					prefix = "<table><tbody><tr><td>";
					path = new[] { "table", "tbody", "tr", "td" };
					break;
				case "thead":
				case "tbody":
				case "tfoot":
					prefix = "<table><" + tag + ">";
					path = new[] { "table", tag };
					break;
				case "table":
					prefix = "<table>";
					path = new[] { "table" };
					break;
				case "select":
					prefix = "<select>";
					path = new[] { "select" };
					break;
				case "colgroup":
					prefix = "<table><colgroup>";
					path = new[] { "table", "colgroup" };
					break;
				case "head":
					path = new[] { "head" };
					break;
				default:
					path = Array.Empty<string>();
					break;
			}
			Document parsed = HtmlParser.Parse(prefix + html);
			Node container = tag == "head" ? parsed.Head : parsed.Body;
			foreach (string step in path)
			{
				if (step == "head")
					continue;
				Element next = null;
				foreach (Node child in container.Children)
				{
					if (child is Element element && element.TagName == step)
					{
						next = element;
						break;
					}
				}
				if (next == null)
					break;
				container = next;
			}
			if (container == null)
				return fragment;
			List<Node> nodes = new List<Node>(container.Children);
			// Leading style, script, link and meta tags are parsed into the head; in a fragment they belong where they were written
			if (tag != "head" && path.Length == 0 && parsed.Head != null && parsed.Head.Children.Count > 0)
				nodes.InsertRange(0, parsed.Head.Children);
			foreach (Node node in nodes)
				fragment.AppendChild(node);
			return fragment;
		}

		private void SetInnerHtml(Element element, string html)
		{
			List<Node> removed = new List<Node>(element.Children);
			foreach (Node child in removed)
				element.RemoveChild(child);
			Element fragment = ParseFragment(html, element);
			List<Node> added = new List<Node>(fragment.Children);
			foreach (Node node in added)
				element.AppendChild(node);
			Mutated(element, "childList", null, added, removed);
			foreach (Node node in added)
				RunInsertedScripts(node);
		}

		// Selectors

		private readonly Dictionary<string, List<Selector>> _selectorCache = new Dictionary<string, List<Selector>>(StringComparer.Ordinal);

		internal List<Selector> ParseSelectors(string text)
		{
			if (_selectorCache.TryGetValue(text, out List<Selector> cached))
				return cached;
			List<Selector> selectors;
			try
			{
				selectors = SelectorParser.ParseList(CssTokenizer.Tokenize(text));
			}
			catch (Exception)
			{
				selectors = null;
			}
			if (selectors == null || selectors.Count == 0)
				throw DomException("SyntaxError", "'" + text + "' is not a valid selector.");
			if (_selectorCache.Count > 500)
				_selectorCache.Clear();
			_selectorCache[text] = selectors;
			return selectors;
		}

		private static bool MatchesAny(Element element, List<Selector> selectors)
		{
			foreach (Selector selector in selectors)
			{
				if (selector.PseudoElement == null && selector.Matches(element))
					return true;
			}
			return false;
		}

		internal bool Matches(Element element, string text) => MatchesAny(element, ParseSelectors(text));

		// The elements below a node in document order, leaving out what sits inside template elements
		internal static IEnumerable<Element> Elements(Node root)
		{
			Stack<Node> pending = new Stack<Node>();
			for (int i = root.Children.Count - 1; i >= 0; i--)
				pending.Push(root.Children[i]);
			while (pending.Count > 0)
			{
				Node node = pending.Pop();
				if (node is not Element element)
					continue;
				yield return element;
				if (element.TagName == "template")
					continue;
				for (int i = node.Children.Count - 1; i >= 0; i--)
					pending.Push(node.Children[i]);
			}
		}

		internal Element QueryFirst(Node root, string text)
		{
			List<Selector> selectors = ParseSelectors(text);
			foreach (Element element in Elements(root))
			{
				if (MatchesAny(element, selectors))
					return element;
			}
			return null;
		}

		internal JsArray QueryAll(Node root, string text)
		{
			List<Selector> selectors = ParseSelectors(text);
			JsArray result = NodeList();
			foreach (Element element in Elements(root))
			{
				if (MatchesAny(element, selectors))
					result.Elements.Add(Wrap(element));
			}
			return result;
		}

		private JsArray ByTag(Node root, string tag)
		{
			tag = tag.ToLowerInvariant();
			JsArray result = NodeList();
			foreach (Element element in Elements(root))
			{
				if (tag == "*" || element.TagName == tag)
					result.Elements.Add(Wrap(element));
			}
			return result;
		}

		private JsArray ByClass(Node root, string classes)
		{
			string[] names = classes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			JsArray result = NodeList();
			foreach (Element element in Elements(root))
			{
				bool all = names.Length > 0;
				foreach (string name in names)
					all &= element.HasClass(name);
				if (all)
					result.Elements.Add(Wrap(element));
			}
			return result;
		}

		private JsArray ElementList(List<Node> nodes)
		{
			JsArray result = NodeList();
			foreach (Node node in nodes)
			{
				if (node is Element element)
					result.Elements.Add(Wrap(element));
			}
			return result;
		}

		private JsObject _nodeListPrototype;

		// A NodeList or HTMLCollection: an array snapshot with item() and namedItem(), since scripts index and iterate them alike
		private JsArray NodeList()
		{
			if (_nodeListPrototype == null)
			{
				_nodeListPrototype = new JsObject(Interpreter.ArrayPrototype);
				_nodeListPrototype.Put(Interpreter.SymbolToStringTag, "NodeList", false);
				Interpreter.NewConstructor("NodeList", 0, _nodeListPrototype, (i, t, a) => throw Interpreter.TypeError("Illegal constructor"), null);
				Interpreter.NewConstructor("HTMLCollection", 0, _nodeListPrototype, (i, t, a) => throw Interpreter.TypeError("Illegal constructor"), null);
				_nodeListPrototype.Put("constructor", Interpreter.Global.Get("NodeList", Interpreter), false);
			}
			JsArray list = new JsArray(_nodeListPrototype);
			list.Put("item", _nodeListItem ??= Interpreter.NewNative("item", 1, (i, t, a) =>
			{
				if (!(t.IsObject && t.Object is JsArray array))
					return JsValue.Null;
				int index = Interpreter.Arg(a, 0).ToInt32();
				return index >= 0 && index < array.Elements.Count ? array.Elements[index] : JsValue.Null;
			}), false);
			list.Put("namedItem", _nodeListNamed ??= Interpreter.NewNative("namedItem", 1, (i, t, a) =>
			{
				if (!(t.IsObject && t.Object is JsArray array))
					return JsValue.Null;
				string name = Interpreter.Arg(a, 0).ToJsString();
				foreach (JsValue item in array.Elements)
				{
					if (Unwrap(item) is Element element && (element.Id == name || element.GetAttribute("name") == name))
						return item;
				}
				return JsValue.Null;
			}), false);
			return list;
		}

		private JsFunction _nodeListItem;
		private JsFunction _nodeListNamed;

		// HTMLElement: the form controls, links, images and the rest of what pages poke at

		private void SetupHtmlElement()
		{
			JsObject proto = HtmlElementPrototype;
			Attribute(proto, "title", "title");
			Attribute(proto, "lang", "lang");
			Attribute(proto, "dir", "dir");
			Attribute(proto, "accessKey", "accesskey");
			Attribute(proto, "name", "name");
			Attribute(proto, "type", "type");
			Attribute(proto, "placeholder", "placeholder");
			Attribute(proto, "alt", "alt");
			Attribute(proto, "target", "target");
			Attribute(proto, "rel", "rel");
			Attribute(proto, "download", "download");
			Attribute(proto, "method", "method", "get");
			Attribute(proto, "enctype", "enctype", "application/x-www-form-urlencoded");
			Attribute(proto, "htmlFor", "for");
			Attribute(proto, "label", "label");
			Attribute(proto, "accept", "accept");
			Attribute(proto, "autocomplete", "autocomplete");
			Attribute(proto, "pattern", "pattern");
			Attribute(proto, "min", "min");
			Attribute(proto, "max", "max");
			Attribute(proto, "step", "step");
			Attribute(proto, "charset", "charset");
			Attribute(proto, "media", "media");
			Attribute(proto, "sizes", "sizes");
			Attribute(proto, "srcset", "srcset");
			Attribute(proto, "loading", "loading");
			Attribute(proto, "crossOrigin", "crossorigin");
			Attribute(proto, "referrerPolicy", "referrerpolicy");
			Attribute(proto, "content", "content");
			Attribute(proto, "httpEquiv", "http-equiv");
			Attribute(proto, "role", "role");
			ElementGetter(proto, "innerText", e => InnerText(e), (e, v) => SetTextContent(e, v.IsNullish ? "" : v.ToJsString()));
			ElementGetter(proto, "outerText", e => InnerText(e), (e, v) => SetTextContent(e, v.IsNullish ? "" : v.ToJsString()));
			BooleanAttribute(proto, "hidden", "hidden");
			BooleanAttribute(proto, "disabled", "disabled");
			BooleanAttribute(proto, "readOnly", "readonly");
			BooleanAttribute(proto, "required", "required");
			BooleanAttribute(proto, "multiple", "multiple");
			BooleanAttribute(proto, "autofocus", "autofocus");
			BooleanAttribute(proto, "defer", "defer");
			BooleanAttribute(proto, "async", "async");
			BooleanAttribute(proto, "noModule", "nomodule");
			BooleanAttribute(proto, "open", "open");
			BooleanAttribute(proto, "controls", "controls");
			BooleanAttribute(proto, "autoplay", "autoplay");
			BooleanAttribute(proto, "loop", "loop");
			BooleanAttribute(proto, "muted", "muted");
			BooleanAttribute(proto, "defaultChecked", "checked");
			BooleanAttribute(proto, "draggable", "draggable");
			BooleanAttribute(proto, "inert", "inert");
			UrlAttribute(proto, "href", "href");
			UrlAttribute(proto, "src", "src");
			UrlAttribute(proto, "action", "action");
			UrlAttribute(proto, "poster", "poster");
			UrlAttribute(proto, "currentSrc", "src");
			ElementGetter(proto, "tabIndex", e => int.TryParse(e.GetAttribute("tabindex"), out int n) ? n : (e.TagName is "a" or "button" or "input" or "select" or "textarea" ? 0 : -1), (e, v) => SetAttribute(e, "tabindex", v.ToJsString()));
			ElementGetter(proto, "contentEditable", e => e.GetAttribute("contenteditable") ?? "inherit", (e, v) => SetAttribute(e, "contenteditable", v.ToJsString()));
			ElementGetter(proto, "isContentEditable", e => e.GetAttribute("contenteditable") is "" or "true");
			ElementGetter(proto, "nonce", e => e.GetAttribute("nonce") ?? "");
			ElementGetter(proto, "style", e => StyleOf(e), (e, v) => StyleOf(e).SetCssText(v.ToJsString()));
			ElementGetter(proto, "dataset", e => new JsDataset(Interpreter.ObjectPrototype, this, e));
			ElementGetter(proto, "offsetParent", e => Wrap(OffsetParent(e)));
			ElementGetter(proto, "offsetWidth", e => Round(LayoutBoxOf(e)?.Width));
			ElementGetter(proto, "offsetHeight", e => Round(LayoutBoxOf(e)?.Height));
			ElementGetter(proto, "offsetTop", e => Round((LayoutBoxOf(e)?.Y ?? 0) - (LayoutBoxOf(OffsetParent(e))?.Y ?? 0)));
			ElementGetter(proto, "offsetLeft", e => Round((LayoutBoxOf(e)?.X ?? 0) - (LayoutBoxOf(OffsetParent(e))?.X ?? 0)));
			Interpreter.DefineMethod(proto, "focus", 0, (i, t, a) =>
			{
				Element element = ThisElement(t);
				Focus?.Invoke(element);
				SetActiveElement(element);
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(proto, "blur", 0, (i, t, a) =>
			{
				if (ActiveElement == ThisElement(t))
				{
					Focus?.Invoke(null);
					SetActiveElement(null);
				}
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(proto, "click", 0, (i, t, a) =>
			{
				Click(ThisElement(t));
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(proto, "submit", 0, (i, t, a) =>
			{
				Element form = ThisElement(t);
				if (form.TagName == "form" && Url != null)
				{
					FormSubmission submission = Forms.Submit(form, Url);
					if (submission != null)
						Navigate?.Invoke(submission);
				}
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(proto, "requestSubmit", 0, (i, t, a) =>
			{
				Element form = ThisElement(t);
				if (form.TagName == "form")
					SubmitForm(Unwrap(Interpreter.Arg(a, 0)) as Element ?? form);
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(proto, "reset", 0, (i, t, a) =>
			{
				Element form = ThisElement(t);
				foreach (Element control in form.DescendantElements())
				{
					if (control.TagName == "input" || control.TagName == "textarea")
						RemoveAttribute(control, "value");
				}
				DispatchSimple("reset", form, true, true);
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(proto, "checkValidity", 0, (i, t, a) => true);
			Interpreter.DefineMethod(proto, "reportValidity", 0, (i, t, a) => true);
			Interpreter.DefineMethod(proto, "setCustomValidity", 1, (i, t, a) => JsValue.Undefined);
			Interpreter.DefineMethod(proto, "select", 0, (i, t, a) => JsValue.Undefined);
			Interpreter.DefineMethod(proto, "setSelectionRange", 2, (i, t, a) => JsValue.Undefined);
			Interpreter.DefineMethod(proto, "getContext", 1, (i, t, a) => CanvasContext(ThisElement(t), Interpreter.Arg(a, 0).ToJsString()));
			Interpreter.DefineMethod(proto, "toDataURL", 0, (i, t, a) => "data:,");
			Interpreter.DefineMethod(proto, "play", 0, (i, t, a) => Interpreter.PromiseResolve(JsValue.Undefined));
			Interpreter.DefineMethod(proto, "pause", 0, (i, t, a) => JsValue.Undefined);
			Interpreter.DefineMethod(proto, "load", 0, (i, t, a) => JsValue.Undefined);
			Interpreter.DefineMethod(proto, "showModal", 0, (i, t, a) =>
			{
				SetAttribute(ThisElement(t), "open", "");
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(proto, "show", 0, (i, t, a) =>
			{
				SetAttribute(ThisElement(t), "open", "");
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(proto, "close", 0, (i, t, a) =>
			{
				RemoveAttribute(ThisElement(t), "open");
				return JsValue.Undefined;
			});
			ElementGetter(proto, "validity", e =>
			{
				JsObject validity = Interpreter.NewObject();
				validity.Put("valid", true);
				validity.Put("valueMissing", false);
				return validity;
			});
			ElementGetter(proto, "validationMessage", e => "");
			ElementGetter(proto, "willValidate", e => e.TagName is "input" or "select" or "textarea" or "button");
			ElementGetter(proto, "value", e => ValueOf(e), (e, v) => SetValue(e, v.IsNullish ? "" : v.ToJsString()));
			ElementGetter(proto, "defaultValue", e => e.GetAttribute("value") ?? (e.TagName == "textarea" ? e.TextContent : ""), (e, v) => SetAttribute(e, "value", v.ToJsString()));
			ElementGetter(proto, "valueAsNumber", e => double.TryParse(ValueOf(e), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : double.NaN, (e, v) => SetValue(e, JsValue.NumberToString(v.ToNumber())));
			ElementGetter(proto, "checked", e => e.HasAttribute("checked"), (e, v) =>
			{
				if (v.Truthy)
				{
					if (!e.HasAttribute("checked"))
					{
						if ((e.GetAttribute("type") ?? "").ToLowerInvariant() == "radio")
							Forms.Toggle(e);
						else
							e.SetAttribute("checked", "");
						Mutated(e, "attributes", "checked");
					}
				}
				else
					RemoveAttribute(e, "checked");
			});
			ElementGetter(proto, "indeterminate", e => false, (e, v) => { });
			ElementGetter(proto, "selected", e => e.HasAttribute("selected"), (e, v) =>
			{
				if (v.Truthy)
				{
					if (e.Parent is Element select)
					{
						foreach (Element other in select.ElementsByTagName("option"))
						{
							if (other != e)
								RemoveAttribute(other, "selected");
						}
					}
					SetAttribute(e, "selected", "");
				}
				else
					RemoveAttribute(e, "selected");
			});
			ElementGetter(proto, "defaultSelected", e => e.HasAttribute("selected"));
			ElementGetter(proto, "text", e => e.TagName == "option" || e.TagName == "title" || e.TagName == "script" ? HtmlText.CollapseWhitespace(e.TextContent).Trim() : e.TextContent, (e, v) => SetTextContent(e, v.ToJsString()));
			ElementGetter(proto, "index", e =>
			{
				int index = 0;
				Element select = ClosestTag(e, "select");
				if (select == null)
					return 0;
				foreach (Element option in select.ElementsByTagName("option"))
				{
					if (option == e)
						return index;
					index++;
				}
				return 0;
			});
			ElementGetter(proto, "options", e => ByTag(e, "option"));
			ElementGetter(proto, "selectedOptions", e =>
			{
				JsArray result = NodeList();
				foreach (Element option in e.ElementsByTagName("option"))
				{
					if (option.HasAttribute("selected"))
						result.Elements.Add(Wrap(option));
				}
				return result;
			});
			ElementGetter(proto, "selectedIndex", e =>
			{
				int index = 0;
				int first = -1;
				foreach (Element option in e.ElementsByTagName("option"))
				{
					if (option.HasAttribute("selected"))
						return index;
					if (first < 0)
						first = 0;
					index++;
				}
				return first;
			}, (e, v) =>
			{
				int wanted = v.ToInt32();
				int index = 0;
				foreach (Element option in e.ElementsByTagName("option"))
				{
					if (index++ == wanted)
						SetAttribute(option, "selected", "");
					else
						RemoveAttribute(option, "selected");
				}
			});
			ElementGetter(proto, "form", e => Wrap(Forms.FormOf(e)));
			ElementGetter(proto, "elements", e =>
			{
				JsArray result = NodeList();
				foreach (Element control in e.DescendantElements())
				{
					if (control.TagName is "input" or "select" or "textarea" or "button" or "fieldset" or "output")
						result.Elements.Add(Wrap(control));
				}
				return result;
			});
			ElementGetter(proto, "length", e =>
			{
				int count = 0;
				foreach (Element control in e.DescendantElements())
				{
					if (e.TagName == "select" ? control.TagName == "option" : control.TagName is "input" or "select" or "textarea" or "button")
						count++;
				}
				return count;
			});
			ElementGetter(proto, "labels", e => Interpreter.NewArray());
			ElementGetter(proto, "files", e => JsValue.Null);
			ElementGetter(proto, "control", e => Wrap(e.GetAttribute("for") is string id ? ElementById(id) : null));
			ElementGetter(proto, "naturalWidth", e => ImageSize(e, true));
			ElementGetter(proto, "naturalHeight", e => ImageSize(e, false));
			ElementGetter(proto, "complete", e => true);
			ElementGetter(proto, "width", e => e.TagName == "img" ? (int.TryParse(e.GetAttribute("width"), out int w) ? w : Round(LayoutBoxOf(e)?.Width)) : (int.TryParse(e.GetAttribute("width"), out int w2) ? w2 : 300), (e, v) => SetAttribute(e, "width", v.ToJsString()));
			ElementGetter(proto, "height", e => e.TagName == "img" ? (int.TryParse(e.GetAttribute("height"), out int h) ? h : Round(LayoutBoxOf(e)?.Height)) : (int.TryParse(e.GetAttribute("height"), out int h2) ? h2 : 150), (e, v) => SetAttribute(e, "height", v.ToJsString()));
			ElementGetter(proto, "content", e =>
			{
				if (e.TagName != "template")
					return e.GetAttribute("content") ?? "";
				Element fragment = new Element(FragmentTag);
				foreach (Node child in e.Children)
					fragment.AppendChild(Clone(child, true));
				return Wrap(fragment);
			}, (e, v) => SetAttribute(e, "content", v.ToJsString()));
			ElementGetter(proto, "sheet", e => e.TagName == "style" || e.TagName == "link" ? StyleSheetObject(e) : JsValue.Null);
			ElementGetter(proto, "contentWindow", e => JsValue.Null);
			ElementGetter(proto, "contentDocument", e => JsValue.Null);
			ElementGetter(proto, "readyState", e => e.TagName is "video" or "audio" ? (JsValue)0 : "complete");
			ElementGetter(proto, "paused", e => true);
			ElementGetter(proto, "duration", e => double.NaN);
			ElementGetter(proto, "currentTime", e => 0, (e, v) => { });
			ElementGetter(proto, "volume", e => 1, (e, v) => { });
			ElementGetter(proto, "textLength", e => ValueOf(e).Length);
			ElementGetter(proto, "selectionStart", e => ValueOf(e).Length, (e, v) => { });
			ElementGetter(proto, "selectionEnd", e => ValueOf(e).Length, (e, v) => { });
			ElementGetter(proto, "rows", e => e.TagName is "table" or "tbody" or "thead" or "tfoot" ? (JsValue)ByTag(e, "tr") : (int.TryParse(e.GetAttribute("rows"), out int r) ? r : 2), (e, v) => SetAttribute(e, "rows", v.ToJsString()));
			ElementGetter(proto, "cells", e => ElementList(e.Children));
			ElementGetter(proto, "tBodies", e => ByTag(e, "tbody"));
			ElementGetter(proto, "cols", e => int.TryParse(e.GetAttribute("cols"), out int c) ? c : 20, (e, v) => SetAttribute(e, "cols", v.ToJsString()));
			ElementGetter(proto, "maxLength", e => int.TryParse(e.GetAttribute("maxlength"), out int m) ? m : -1, (e, v) => SetAttribute(e, "maxlength", v.ToJsString()));
			ElementGetter(proto, "size", e => int.TryParse(e.GetAttribute("size"), out int s) ? s : 20, (e, v) => SetAttribute(e, "size", v.ToJsString()));
			ElementGetter(proto, "span", e => int.TryParse(e.GetAttribute("span"), out int s) ? s : 1, (e, v) => SetAttribute(e, "span", v.ToJsString()));
			ElementGetter(proto, "colSpan", e => int.TryParse(e.GetAttribute("colspan"), out int s) ? s : 1, (e, v) => SetAttribute(e, "colspan", v.ToJsString()));
			ElementGetter(proto, "rowSpan", e => int.TryParse(e.GetAttribute("rowspan"), out int s) ? s : 1, (e, v) => SetAttribute(e, "rowspan", v.ToJsString()));
			ElementGetter(proto, "cellIndex", e => e.Parent?.Children.IndexOf(e) ?? -1);
			ElementGetter(proto, "rowIndex", e => e.Parent?.Children.IndexOf(e) ?? -1);
			ElementGetter(proto, "start", e => int.TryParse(e.GetAttribute("start"), out int s) ? s : 1, (e, v) => SetAttribute(e, "start", v.ToJsString()));
			Interpreter.DefineMethod(proto, "insertRow", 0, (i, t, a) =>
			{
				Element table = ThisElement(t);
				Element row = new Element("tr");
				Insert(table, row, null);
				return Wrap(row);
			});
			Interpreter.DefineMethod(proto, "insertCell", 0, (i, t, a) =>
			{
				Element cell = new Element("td");
				Insert(ThisElement(t), cell, null);
				return Wrap(cell);
			});
			Interpreter.DefineMethod(proto, "add", 1, (i, t, a) =>
			{
				Node option = Unwrap(Interpreter.Arg(a, 0));
				if (option != null)
					Insert(ThisElement(t), option, null);
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(proto, "item", 1, (i, t, a) =>
			{
				int index = Interpreter.Arg(a, 0).ToInt32();
				foreach (Element option in ThisElement(t).ElementsByTagName("option"))
				{
					if (index-- == 0)
						return Wrap(option);
				}
				return JsValue.Null;
			});
			Interpreter.DefineMethod(proto, "namedItem", 1, (i, t, a) =>
			{
				string name = Interpreter.Arg(a, 0).ToJsString();
				foreach (Element option in ThisElement(t).ElementsByTagName("option"))
				{
					if (option.Id == name || option.GetAttribute("name") == name)
						return Wrap(option);
				}
				return JsValue.Null;
			});
			foreach (string part in new[] { "protocol", "host", "hostname", "port", "pathname", "search", "hash", "origin", "username", "password" })
			{
				string name = part;
				ElementGetter(proto, name, e =>
				{
					Uri resolved = Resolve((e.GetAttribute("href") ?? "").Trim());
					return UrlPart(resolved, name);
				}, (e, v) =>
				{
					Uri resolved = Resolve((e.GetAttribute("href") ?? "").Trim());
					if (resolved != null)
						SetAttribute(e, "href", WithUrlPart(resolved, name, v.ToJsString()).ToString());
				});
			}
		}

		private static Element ClosestTag(Node node, string tag)
		{
			for (Node n = node; n != null; n = n.Parent)
			{
				if (n is Element element && element.TagName == tag)
					return element;
			}
			return null;
		}

		private string InnerText(Element element)
		{
			StringBuilder text = new StringBuilder();
			AppendInnerText(element, text);
			return HtmlText.CollapseWhitespace(text.ToString()).Trim();
		}

		private static void AppendInnerText(Node node, StringBuilder text)
		{
			foreach (Node child in node.Children)
			{
				if (child is TextNode t)
					text.Append(t.Data);
				else if (child is Element element)
				{
					if (element.TagName is "script" or "style" or "template" or "noscript")
						continue;
					bool block = element.TagName is "p" or "div" or "br" or "li" or "tr" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "section" or "article" or "header" or "footer" or "ul" or "ol" or "table" or "pre" or "blockquote";
					if (block)
						text.Append('\n');
					AppendInnerText(element, text);
					if (block)
						text.Append('\n');
				}
			}
		}

		private Element OffsetParent(Element element)
		{
			for (Node n = element.Parent; n != null; n = n.Parent)
			{
				if (n is Element candidate && (candidate.TagName == "body" || (LayoutBoxOf(candidate)?.Style?.Position ?? Position.Static) != Position.Static))
					return candidate;
			}
			return null;
		}

		private JsValue ImageSize(Element element, bool width)
		{
			LayoutBox box = LayoutBoxOf(element);
			if (box?.Image != null)
				return width ? box.Image.Width : box.Image.Height;
			return Round(width ? box?.Width : box?.Height);
		}

		internal string ValueOf(Element element)
		{
			if (element.TagName == "option")
				return element.GetAttribute("value") ?? HtmlText.CollapseWhitespace(element.TextContent).Trim();
			if (element.TagName == "input" || element.TagName == "textarea" || element.TagName == "select")
				return Forms.ValueOf(element);
			if (element.TagName == "button" || element.TagName == "li" || element.TagName == "progress" || element.TagName == "meter" || element.TagName == "data" || element.TagName == "output")
				return element.GetAttribute("value") ?? "";
			return element.GetAttribute("value") ?? "";
		}

		internal void SetValue(Element element, string value)
		{
			if (element.TagName == "select")
			{
				bool found = false;
				foreach (Element option in element.ElementsByTagName("option"))
				{
					bool match = !found && ValueOf(option) == value;
					if (match)
					{
						found = true;
						SetAttribute(option, "selected", "");
					}
					else
						RemoveAttribute(option, "selected");
				}
				return;
			}
			if (element.TagName == "textarea" && !element.HasAttribute("value") && element.TextContent == value)
				return;
			SetAttribute(element, "value", value);
		}

		// A script-driven click: the event, then what the element does by default when nothing prevented it
		internal void Click(Element element)
		{
			if (element.HasAttribute("disabled"))
				return;
			if (!DispatchMouse("click", element, 0, 0))
				return;
			PerformClickDefault(element);
		}

		public void PerformClickDefault(Element element)
		{
			for (Node n = element; n is Element current; n = n.Parent)
			{
				if (current.TagName == "a" && current.GetAttribute("href") is string href)
				{
					Uri target = Resolve(href.Trim());
					if (target != null && Url != null && href.StartsWith("#", StringComparison.Ordinal))
					{
						SetHash(href);
						return;
					}
					if (target != null && Navigate != null)
						Navigate(new FormSubmission { Url = target, Method = "GET" });
					return;
				}
				if (current.TagName == "label")
				{
					Element control = current.GetAttribute("for") is string id ? ElementById(id) : null;
					if (control == null)
					{
						foreach (Element inner in current.DescendantElements())
						{
							if (inner.TagName is "input" or "select" or "textarea" or "button")
							{
								control = inner;
								break;
							}
						}
					}
					if (control != null && control != element)
						Click(control);
					return;
				}
				if (current == element && Forms.IsCheckable(element))
				{
					Forms.Toggle(element);
					Mutated(element, "attributes", "checked");
					DispatchInput(element, null);
					DispatchSimple("change", element, true, false);
					return;
				}
				if (current == element && Forms.IsSubmitButton(element))
				{
					SubmitForm(element);
					return;
				}
				if (current == element && element.TagName == "summary" && element.Parent is Element { TagName: "details" } details)
				{
					if (details.HasAttribute("open"))
						RemoveAttribute(details, "open");
					else
						SetAttribute(details, "open", "");
					DispatchSimple("toggle", details, false, false);
					return;
				}
			}
		}

		// A form submission from a control: the submit event first, then the navigation unless it was cancelled
		public bool SubmitForm(Element control)
		{
			Element form = control.TagName == "form" ? control : Forms.FormOf(control);
			if (form == null || Url == null)
				return false;
			JsEvent ev = NewEvent("submit", true, true);
			ev.Put("submitter", control == form ? JsValue.Null : Wrap(control));
			if (!DispatchEvent(ev, form))
				return false;
			FormSubmission submission = Forms.Submit(control == form ? FirstSubmitter(form) ?? form : control, Url);
			if (submission == null)
				return false;
			Navigate?.Invoke(submission);
			return true;
		}

		private static Element FirstSubmitter(Element form)
		{
			foreach (Element control in form.DescendantElements())
			{
				if (Forms.IsSubmitButton(control))
					return control;
			}
			return null;
		}

		// Document

		private void SetupDocumentPrototype()
		{
			DocumentPrototype = NewPrototype(NodePrototype, "Document");
			JsObject htmlDocument = NewPrototype(DocumentPrototype, "HTMLDocument");
			FragmentPrototype = NewPrototype(ElementPrototype, "DocumentFragment");
			ShadowRootPrototype = NewPrototype(FragmentPrototype, "ShadowRoot");
			Getter(ShadowRootPrototype, "host", n => Wrap(n.Parent));
			Getter(ShadowRootPrototype, "delegatesFocus", n => false);
			Getter(ShadowRootPrototype, "adoptedStyleSheets", n => Interpreter.NewArray(), (n, v) => { });
			Getter(ShadowRootPrototype, "activeElement", n => Wrap((Node)ActiveElement ?? null));
			Interpreter.Global.Get("DocumentFragment", Interpreter).Function.NativeNew = (i, a, target) => Wrap(new Element(FragmentTag)).Object;
			Interpreter.Global.Get("Document", Interpreter).Function.NativeNew = (i, a, target) => Wrap(new Document()).Object;
			JsObject proto = DocumentPrototype;
			Getter(proto, "documentElement", n => Wrap(((Document)n).DocumentElement));
			Getter(proto, "head", n => Wrap(((Document)n).Head));
			Getter(proto, "body", n => Wrap(((Document)n).Body), (n, v) =>
			{
				Document document = (Document)n;
				if (Unwrap(v) is Element body && document.DocumentElement != null)
				{
					Element old = document.Body;
					if (old != null)
						Remove(old);
					Insert(document.DocumentElement, body, null);
				}
			});
			Getter(proto, "title", n => ((Document)n).Title, (n, v) =>
			{
				Document document = (Document)n;
				Element title = document.DocumentElement?.FirstElement("title");
				if (title == null)
				{
					if (document.Head == null)
						return;
					title = new Element("title");
					document.Head.AppendChild(title);
				}
				SetTextContent(title, v.ToJsString());
				TitleChanged = true;
			});
			Getter(proto, "readyState", n => ReadyState);
			Getter(proto, "defaultView", n => Interpreter.Global);
			Getter(proto, "activeElement", n => Wrap((Node)ActiveElement ?? Document.Body));
			Getter(proto, "currentScript", n => Wrap(_currentScript));
			Getter(proto, "URL", n => Url?.ToString() ?? "about:blank");
			Getter(proto, "documentURI", n => Url?.ToString() ?? "about:blank");
			Getter(proto, "baseURI", n => Url?.ToString() ?? "about:blank");
			Getter(proto, "referrer", n => "");
			Getter(proto, "domain", n => Url?.Host ?? "");
			Getter(proto, "characterSet", n => "UTF-8");
			Getter(proto, "charset", n => "UTF-8");
			Getter(proto, "inputEncoding", n => "UTF-8");
			Getter(proto, "contentType", n => "text/html");
			Getter(proto, "compatMode", n => ((Document)n).QuirksMode ? "BackCompat" : "CSS1Compat");
			Getter(proto, "doctype", n => JsValue.Null);
			Getter(proto, "hidden", n => false);
			Getter(proto, "visibilityState", n => "visible");
			Getter(proto, "fullscreenElement", n => JsValue.Null);
			Getter(proto, "pointerLockElement", n => JsValue.Null);
			Getter(proto, "scrollingElement", n => Wrap(((Document)n).DocumentElement));
			Getter(proto, "designMode", n => "off", (n, v) => { });
			Getter(proto, "dir", n => ((Document)n).DocumentElement?.GetAttribute("dir") ?? "", (n, v) => { });
			Getter(proto, "children", n => ElementList(n.Children));
			Getter(proto, "childElementCount", n => n.Children.Count);
			Getter(proto, "firstElementChild", n => Wrap(FirstElementChild(n, false)));
			Getter(proto, "lastElementChild", n => Wrap(FirstElementChild(n, true)));
			Getter(proto, "forms", n => ByTag(n, "form"));
			Getter(proto, "images", n => ByTag(n, "img"));
			Getter(proto, "links", n => ByTag(n, "a"));
			Getter(proto, "anchors", n => ByTag(n, "a"));
			Getter(proto, "scripts", n => ByTag(n, "script"));
			Getter(proto, "styleSheets", n =>
			{
				JsArray sheets = Interpreter.NewArray();
				foreach (Element element in n.DescendantElements())
				{
					if (element.TagName == "style" || element.TagName == "link" && (element.GetAttribute("rel") ?? "").Contains("stylesheet", StringComparison.OrdinalIgnoreCase))
						sheets.Elements.Add(StyleSheetObject(element));
				}
				return sheets;
			});
			Getter(proto, "cookie", n => CookieHeader(), (n, v) => SetCookie(v.ToJsString()));
			Getter(proto, "location", n => Interpreter.Global.Get("location", Interpreter), (n, v) => LocationAssign(v.ToJsString()));
			Getter(proto, "fonts", n =>
			{
				JsObject fonts = Interpreter.NewObject();
				fonts.Put("ready", Interpreter.PromiseResolve(fonts));
				fonts.Put("status", "loaded");
				Interpreter.DefineMethod(fonts, "load", 1, (i, t, a) => Interpreter.PromiseResolve(Interpreter.NewArray()));
				Interpreter.DefineMethod(fonts, "check", 1, (i, t, a) => true);
				Interpreter.DefineMethod(fonts, "addEventListener", 2, (i, t, a) => JsValue.Undefined);
				Interpreter.DefineMethod(fonts, "add", 1, (i, t, a) => fonts);
				return fonts;
			});
			Getter(proto, "implementation", n =>
			{
				JsObject implementation = Interpreter.NewObject();
				Interpreter.DefineMethod(implementation, "createHTMLDocument", 1, (i, t, a) =>
				{
					Document created = HtmlParser.Parse("<html><head><title>" + Interpreter.Arg(a, 0).ToJsString() + "</title></head><body></body></html>");
					return Wrap(created);
				});
				Interpreter.DefineMethod(implementation, "hasFeature", 0, (i, t, a) => true);
				return implementation;
			});
			Getter(proto, "timeline", n =>
			{
				JsObject timeline = Interpreter.NewObject();
				timeline.Put("currentTime", Now);
				return timeline;
			});
			Interpreter.DefineMethod(proto, "createElement", 1, (i, t, a) =>
			{
				string tag = Interpreter.Arg(a, 0).ToJsString().ToLowerInvariant();
				if (tag.Length == 0)
					throw DomException("InvalidCharacterError", "The tag name provided is not a valid name.");
				if (_customElements.ContainsKey(tag))
					return ConstructRegistered(tag, null);
				return Wrap(new Element(tag));
			});
			Interpreter.DefineMethod(proto, "createElementNS", 2, (i, t, a) => Wrap(new Element(Interpreter.Arg(a, 1).ToJsString().ToLowerInvariant())));
			Interpreter.DefineMethod(proto, "createTextNode", 1, (i, t, a) => Wrap(new TextNode(Interpreter.Arg(a, 0).ToJsString())));
			Interpreter.DefineMethod(proto, "createComment", 1, (i, t, a) => Wrap(new Comment(Interpreter.Arg(a, 0).ToJsString())));
			Interpreter.DefineMethod(proto, "createDocumentFragment", 0, (i, t, a) => Wrap(new Element(FragmentTag)));
			Interpreter.DefineMethod(proto, "createAttribute", 1, (i, t, a) =>
			{
				JsObject attr = Interpreter.NewObject();
				attr.Put("name", Interpreter.Arg(a, 0).ToJsString().ToLowerInvariant());
				attr.Put("value", "");
				return attr;
			});
			Interpreter.DefineMethod(proto, "createEvent", 1, (i, t, a) =>
			{
				string kind = Interpreter.Arg(a, 0).ToJsString().ToLowerInvariant();
				JsObject prototype = kind.StartsWith("mouse", StringComparison.Ordinal) ? MouseEventPrototype : kind.StartsWith("keyboard", StringComparison.Ordinal) ? KeyboardEventPrototype : kind.StartsWith("custom", StringComparison.Ordinal) ? CustomEventPrototype : EventPrototype;
				return new JsEvent(prototype) { Type = "", TimeStamp = Now };
			});
			Interpreter.DefineMethod(proto, "createRange", 0, (i, t, a) =>
			{
				JsObject range = Interpreter.NewObject();
				range.Class = "Range";
				Interpreter.DefineMethod(range, "selectNode", 1, (ii, tt, aa) => JsValue.Undefined);
				Interpreter.DefineMethod(range, "selectNodeContents", 1, (ii, tt, aa) => JsValue.Undefined);
				Interpreter.DefineMethod(range, "setStart", 2, (ii, tt, aa) => JsValue.Undefined);
				Interpreter.DefineMethod(range, "setEnd", 2, (ii, tt, aa) => JsValue.Undefined);
				Interpreter.DefineMethod(range, "collapse", 0, (ii, tt, aa) => JsValue.Undefined);
				Interpreter.DefineMethod(range, "getBoundingClientRect", 0, (ii, tt, aa) => BoundingRect(Document.Body ?? Document.DocumentElement));
				Interpreter.DefineMethod(range, "getClientRects", 0, (ii, tt, aa) => Interpreter.NewArray());
				Interpreter.DefineMethod(range, "createContextualFragment", 1, (ii, tt, aa) => Wrap(ParseFragment(Interpreter.Arg(aa, 0).ToJsString(), Document.Body)));
				Interpreter.DefineMethod(range, "cloneContents", 0, (ii, tt, aa) => Wrap(new Element(FragmentTag)));
				Interpreter.DefineMethod(range, "deleteContents", 0, (ii, tt, aa) => JsValue.Undefined);
				Interpreter.DefineMethod(range, "toString", 0, (ii, tt, aa) => "");
				return range;
			});
			Interpreter.DefineMethod(proto, "createTreeWalker", 1, (i, t, a) => TreeWalker(RequireNode(Interpreter.Arg(a, 0)), Interpreter.Arg(a, 1).IsUndefined ? -1 : Interpreter.Arg(a, 1).ToInt32(), Interpreter.Arg(a, 2)));
			Interpreter.DefineMethod(proto, "createNodeIterator", 1, (i, t, a) => TreeWalker(RequireNode(Interpreter.Arg(a, 0)), Interpreter.Arg(a, 1).IsUndefined ? -1 : Interpreter.Arg(a, 1).ToInt32(), Interpreter.Arg(a, 2)));
			Interpreter.DefineMethod(proto, "getElementById", 1, (i, t, a) =>
			{
				string id = Interpreter.Arg(a, 0).ToJsString();
				foreach (Element element in Elements(ThisNode(t)))
				{
					if (element.Id == id)
						return Wrap(element);
				}
				return JsValue.Null;
			});
			Interpreter.DefineMethod(proto, "getElementsByName", 1, (i, t, a) =>
			{
				string name = Interpreter.Arg(a, 0).ToJsString();
				JsArray result = NodeList();
				foreach (Element element in ThisNode(t).DescendantElements())
				{
					if (element.GetAttribute("name") == name)
						result.Elements.Add(Wrap(element));
				}
				return result;
			});
			Interpreter.DefineMethod(proto, "querySelector", 1, (i, t, a) => Wrap(QueryFirst(ThisNode(t), Interpreter.Arg(a, 0).ToJsString())));
			Interpreter.DefineMethod(proto, "querySelectorAll", 1, (i, t, a) => QueryAll(ThisNode(t), Interpreter.Arg(a, 0).ToJsString()));
			Interpreter.DefineMethod(proto, "getElementsByTagName", 1, (i, t, a) => ByTag(ThisNode(t), Interpreter.Arg(a, 0).ToJsString()));
			Interpreter.DefineMethod(proto, "getElementsByTagNameNS", 2, (i, t, a) => ByTag(ThisNode(t), Interpreter.Arg(a, 1).ToJsString()));
			Interpreter.DefineMethod(proto, "getElementsByClassName", 1, (i, t, a) => ByClass(ThisNode(t), Interpreter.Arg(a, 0).ToJsString()));
			Interpreter.DefineMethod(proto, "elementFromPoint", 2, (i, t, a) => JsValue.Null);
			Interpreter.DefineMethod(proto, "elementsFromPoint", 2, (i, t, a) => Interpreter.NewArray());
			Interpreter.DefineMethod(proto, "hasFocus", 0, (i, t, a) => true);
			Interpreter.DefineMethod(proto, "execCommand", 1, (i, t, a) => false);
			Interpreter.DefineMethod(proto, "queryCommandSupported", 1, (i, t, a) => false);
			Interpreter.DefineMethod(proto, "getSelection", 0, (i, t, a) => SelectionObject());
			Interpreter.DefineMethod(proto, "adoptNode", 1, (i, t, a) => Interpreter.Arg(a, 0));
			Interpreter.DefineMethod(proto, "importNode", 1, (i, t, a) => Wrap(Clone(RequireNode(Interpreter.Arg(a, 0)), Interpreter.Arg(a, 1).Truthy)));
			Interpreter.DefineMethod(proto, "open", 0, (i, t, a) => t);
			Interpreter.DefineMethod(proto, "close", 0, (i, t, a) => JsValue.Undefined);
			Interpreter.DefineMethod(proto, "write", 1, (i, t, a) =>
			{
				StringBuilder text = new StringBuilder();
				foreach (JsValue v in a)
					text.Append(v.ToJsString());
				DocumentWrite(text.ToString());
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(proto, "writeln", 1, (i, t, a) =>
			{
				StringBuilder text = new StringBuilder();
				foreach (JsValue v in a)
					text.Append(v.ToJsString());
				DocumentWrite(text.Append('\n').ToString());
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(proto, "exitFullscreen", 0, (i, t, a) => Interpreter.PromiseResolve(JsValue.Undefined));
			Interpreter.DefineMethod(proto, "append", 1, (i, t, a) =>
			{
				foreach (JsValue item in a)
					Insert(ThisNode(t), NodeOrText(item), null);
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(proto, "prepend", 1, (i, t, a) =>
			{
				Node first = ThisNode(t).FirstChild;
				foreach (JsValue item in a)
					Insert(ThisNode(t), NodeOrText(item), first);
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(proto, "replaceChildren", 0, (i, t, a) =>
			{
				SetTextContent(ThisNode(t), "");
				foreach (JsValue item in a)
					Insert(ThisNode(t), NodeOrText(item), null);
				return JsValue.Undefined;
			});
			_ = htmlDocument;
		}

		// document.write while a script runs puts the markup after that script; later it lands at the end of the body
		private void DocumentWrite(string html)
		{
			Element context = _currentScript?.Parent as Element ?? Document.Body;
			if (context == null)
				return;
			Element fragment = ParseFragment(html, context);
			Node reference = _currentScript != null && _currentScript.Parent == context ? Sibling(_currentScript, 1) : null;
			Insert(context, fragment, reference);
		}

		private JsValue TreeWalker(Node root, int whatToShow, JsValue filter)
		{
			List<Node> nodes = new List<Node>();
			foreach (Node node in root.Descendants())
			{
				int type = NodeTypeOf(node);
				if (whatToShow != -1 && ((whatToShow >> (type - 1)) & 1) == 0)
					continue;
				if (filter.IsCallable || filter.IsObject && Interpreter.GetMember(filter, "acceptNode").IsCallable)
				{
					JsValue acceptNode = filter.IsCallable ? filter : Interpreter.GetMember(filter, "acceptNode");
					if (Interpreter.Call(acceptNode, filter, new[] { Wrap(node) }).ToInt32() != 1)
						continue;
				}
				nodes.Add(node);
			}
			JsObject walker = Interpreter.NewObject();
			walker.Class = "TreeWalker";
			int position = -1;
			walker.Put("root", Wrap(root));
			Interpreter.DefineGetter(walker, "currentNode", (i, t, a) => Wrap(position < 0 ? root : nodes[position]), (i, t, a) =>
			{
				position = nodes.IndexOf(Unwrap(Interpreter.Arg(a, 0)));
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(walker, "nextNode", 0, (i, t, a) => position + 1 < nodes.Count ? Wrap(nodes[++position]) : JsValue.Null);
			Interpreter.DefineMethod(walker, "previousNode", 0, (i, t, a) => position > 0 ? Wrap(nodes[--position]) : JsValue.Null);
			Interpreter.DefineMethod(walker, "firstChild", 0, (i, t, a) =>
			{
				Node current = position < 0 ? root : nodes[position];
				foreach (Node child in current.Children)
				{
					int index = nodes.IndexOf(child);
					if (index >= 0)
					{
						position = index;
						return Wrap(child);
					}
				}
				return JsValue.Null;
			});
			Interpreter.DefineMethod(walker, "parentNode", 0, (i, t, a) =>
			{
				Node current = position < 0 ? root : nodes[position];
				int index = current.Parent == null ? -1 : nodes.IndexOf(current.Parent);
				if (index < 0)
					return JsValue.Null;
				position = index;
				return Wrap(current.Parent);
			});
			Interpreter.DefineMethod(walker, "nextSibling", 0, (i, t, a) =>
			{
				Node current = position < 0 ? root : nodes[position];
				for (Node sibling = Sibling(current, 1); sibling != null; sibling = Sibling(sibling, 1))
				{
					int index = nodes.IndexOf(sibling);
					if (index >= 0)
					{
						position = index;
						return Wrap(sibling);
					}
				}
				return JsValue.Null;
			});
			return walker;
		}

		private JsValue SelectionObject()
		{
			JsObject selection = Interpreter.NewObject();
			selection.Class = "Selection";
			selection.Put("rangeCount", 0);
			selection.Put("type", "None");
			selection.Put("anchorNode", JsValue.Null);
			selection.Put("focusNode", JsValue.Null);
			selection.Put("isCollapsed", true);
			Interpreter.DefineMethod(selection, "toString", 0, (i, t, a) => "");
			Interpreter.DefineMethod(selection, "removeAllRanges", 0, (i, t, a) => JsValue.Undefined);
			Interpreter.DefineMethod(selection, "addRange", 1, (i, t, a) => JsValue.Undefined);
			Interpreter.DefineMethod(selection, "getRangeAt", 1, (i, t, a) => throw DomException("IndexSizeError", "No range"));
			Interpreter.DefineMethod(selection, "selectAllChildren", 1, (i, t, a) => JsValue.Undefined);
			Interpreter.DefineMethod(selection, "collapse", 1, (i, t, a) => JsValue.Undefined);
			return selection;
		}

		private JsValue StyleSheetObject(Element element)
		{
			JsObject sheet = Interpreter.NewObject();
			sheet.Class = "CSSStyleSheet";
			sheet.Put("ownerNode", Wrap(element));
			sheet.Put("href", element.GetAttribute("href") is string href ? Resolve(href)?.ToString() ?? href : JsValue.Null);
			sheet.Put("disabled", false);
			sheet.Put("media", Interpreter.NewArray());
			sheet.Put("cssRules", Interpreter.NewArray());
			sheet.Put("rules", Interpreter.NewArray());
			Interpreter.DefineMethod(sheet, "insertRule", 1, (i, t, a) =>
			{
				if (element.TagName == "style")
				{
					TextNode text = new TextNode(Interpreter.Arg(a, 0).ToJsString() + "\n");
					Insert(element, text, Interpreter.Arg(a, 1).IsUndefined ? null : element.FirstChild);
				}
				return 0;
			});
			Interpreter.DefineMethod(sheet, "deleteRule", 1, (i, t, a) => JsValue.Undefined);
			Interpreter.DefineMethod(sheet, "addRule", 2, (i, t, a) =>
			{
				if (element.TagName == "style")
					Insert(element, new TextNode(Interpreter.Arg(a, 0).ToJsString() + " { " + Interpreter.Arg(a, 1).ToJsString() + " }\n"), null);
				return -1;
			});
			return sheet;
		}

		// Text and Comment

		private void SetupTextAndComment()
		{
			JsObject characterData = NewPrototype(NodePrototype, "CharacterData");
			TextPrototype = NewPrototype(characterData, "Text");
			CommentPrototype = NewPrototype(characterData, "Comment");
			Interpreter.Global.Get("Text", Interpreter).Function.NativeNew = (i, a, target) => Wrap(new TextNode(Interpreter.Arg(a, 0).IsUndefined ? "" : a[0].ToJsString())).Object;
			Interpreter.Global.Get("Comment", Interpreter).Function.NativeNew = (i, a, target) => Wrap(new Comment(Interpreter.Arg(a, 0).IsUndefined ? "" : a[0].ToJsString())).Object;
			Getter(characterData, "data", n => n is TextNode text ? text.Data : ((Comment)n).Data, (n, v) =>
			{
				if (n is TextNode text)
				{
					text.Data = v.ToJsString();
					Mutated(n, "characterData");
				}
			});
			Getter(characterData, "length", n => (n is TextNode text ? text.Data : ((Comment)n).Data).Length);
			Getter(characterData, "wholeText", n => n.TextContent);
			Getter(characterData, "nextElementSibling", n => Wrap(ElementSibling(n, 1)));
			Getter(characterData, "previousElementSibling", n => Wrap(ElementSibling(n, -1)));
			Interpreter.DefineMethod(characterData, "appendData", 1, (i, t, a) =>
			{
				if (ThisNode(t) is TextNode text)
				{
					text.Data += Interpreter.Arg(a, 0).ToJsString();
					Mutated(text, "characterData");
				}
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(characterData, "substringData", 2, (i, t, a) =>
			{
				string data = ThisNode(t) is TextNode text ? text.Data : "";
				int offset = Math.Clamp(Interpreter.Arg(a, 0).ToInt32(), 0, data.Length);
				int count = Math.Clamp(Interpreter.Arg(a, 1).ToInt32(), 0, data.Length - offset);
				return data.Substring(offset, count);
			});
			Interpreter.DefineMethod(characterData, "splitText", 1, (i, t, a) =>
			{
				TextNode text = ThisNode(t) as TextNode;
				if (text == null)
					return JsValue.Null;
				int offset = Math.Clamp(Interpreter.Arg(a, 0).ToInt32(), 0, text.Data.Length);
				TextNode rest = new TextNode(text.Data.Substring(offset));
				text.Data = text.Data.Substring(0, offset);
				if (text.Parent != null)
					Insert(text.Parent, rest, Sibling(text, 1));
				Mutated(text, "characterData");
				return Wrap(rest);
			});
			Interpreter.DefineMethod(characterData, "remove", 0, (i, t, a) =>
			{
				Remove(ThisNode(t));
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(characterData, "before", 1, (i, t, a) =>
			{
				Node self = ThisNode(t);
				if (self.Parent != null)
				{
					foreach (JsValue item in a)
						Insert(self.Parent, NodeOrText(item), self);
				}
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(characterData, "after", 1, (i, t, a) =>
			{
				Node self = ThisNode(t);
				if (self.Parent != null)
				{
					Node reference = Sibling(self, 1);
					foreach (JsValue item in a)
						Insert(self.Parent, NodeOrText(item), reference);
				}
				return JsValue.Undefined;
			});
			Interpreter.DefineMethod(characterData, "replaceWith", 1, (i, t, a) =>
			{
				Node self = ThisNode(t);
				if (self.Parent != null)
				{
					Node parent = self.Parent;
					Node reference = Sibling(self, 1);
					Remove(self);
					foreach (JsValue item in a)
						Insert(parent, NodeOrText(item), reference);
				}
				return JsValue.Undefined;
			});
		}
	}
}
