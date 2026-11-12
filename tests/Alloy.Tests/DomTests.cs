using System;
using System.Collections.Generic;
using System.Text;
using Alloy.Sdk.Script;
using Alloy.Sdk.Web;
using Alloy.Sdk.Web.Css;
using Alloy.Sdk.Web.Dom;
using Alloy.Sdk.Web.Html;
using Alloy.Sdk.Web.Layout;

namespace Alloy.Tests;

// The DOM as scripts see it: a page's scripts run against the parsed document, and the host dispatches events into them
public class DomTests
{
	private const string Url = "https://example.test/dir/page.html?x=1";

	private sealed class Harness
	{
		public ScriptHost Host;
		public List<string> Log = new List<string>();
		public List<FormSubmission> Navigations = new List<FormSubmission>();
		public LayoutBox Root;
		public double Now;
	}

	// A host over markup, with layout on demand from the fixed-advance font and a fake network
	private static Harness Open(string html, ResourceFetcher fetch = null, bool run = true)
	{
		Harness harness = new Harness();
		Document document = HtmlParser.Parse(html);
		ScriptHost host = new ScriptHost(document, new Uri(Url)) { Fetch = fetch };
		harness.Host = host;
		host.Interpreter.Clock = () => harness.Now;
		host.Console = (level, text) => harness.Log.Add(text);
		host.Navigate = submission => harness.Navigations.Add(submission);
		StyleResolver Resolver()
		{
			MediaEnvironment media = new MediaEnvironment(800, 600);
			StyleResolver resolver = new StyleResolver(media);
			StringBuilder css = new StringBuilder("body { margin: 0 } ");
			foreach (Element style in document.DescendantElementsWithShadow())
			{
				if (style.TagName == "style")
					css.Append(style.TextContent);
			}
			resolver.AddStylesheet(CssParser.Parse(css.ToString(), media));
			return resolver;
		}
		host.StyleFor = element => Resolver().Resolve(element);
		host.EnsureLayout = () =>
		{
			StyleResolver resolver = Resolver();
			harness.Root = new BoxBuilder(resolver).Build(document);
			new LayoutEngine(new FixedFontProvider(), 800, 600).Layout(harness.Root);
			host.Dirty = false;
		};
		host.BoxFor = element => Find(harness.Root, element);
		if (run)
			host.RunScripts();
		return harness;
	}

	private static LayoutBox Find(LayoutBox box, Element element)
	{
		if (box == null)
			return null;
		if (box.Node == element && box.Kind != BoxKind.Text)
			return box;
		foreach (LayoutBox child in box.Children)
		{
			LayoutBox found = Find(child, element);
			if (found != null)
				return found;
		}
		return null;
	}

	private static string Eval(Harness harness, string expression) => harness.Host.Interpreter.Evaluate(expression).ToJsString();

	[Fact]
	public void ComputedValuesWithoutGeometryForceNoLayout()
	{
		Harness harness = Open("<style>p { color: rgb(1, 2, 3); opacity: 0.5 }</style><p id=p>x</p>");
		Assert.Contains("1, 2, 3", Eval(harness, "getComputedStyle(document.getElementById('p')).color"));
		Assert.Equal("0.5", Eval(harness, "getComputedStyle(document.getElementById('p')).opacity"));
		Assert.Equal(0, harness.Host.ForcedLayouts);
		Assert.Equal("800px", Eval(harness, "getComputedStyle(document.getElementById('p')).width"));
		Assert.Equal(1, harness.Host.ForcedLayouts);
	}

	[Fact]
	public void AnAwaitThatCannotProgressRunsTheNextScript()
	{
		Harness harness = Open("<script>window.p = new Promise(r => window.resolveIt = r); (async () => { document.title = 'got ' + await window.p; })();</script><script type=\"module\">window.resolveIt('late');</script><p>x</p>");
		Assert.Equal("got late", harness.Host.Document.Title);
	}

	[Fact]
	public void MutatesTheDocument()
	{
		Harness h = Open(@"<title>Old</title><div id=a class='x y'>hi</div><script>
			const a = document.getElementById('a');
			a.textContent = 'hello';
			const b = document.createElement('p');
			b.id = 'b';
			b.innerHTML = '<b>bold</b> text';
			a.after(b);
			a.classList.add('z');
			a.classList.remove('x');
			a.setAttribute('data-count', '3');
			a.dataset.other = 'v';
			a.style.color = 'red';
			a.style.marginTop = '10px';
			document.title = 'New';
			window.result = [a.className, a.dataset.count, a.getAttribute('data-other'), a.style.cssText, document.querySelectorAll('p b').length, b.previousElementSibling === a, document.title, a.outerHTML, b.textContent, a.matches('.z'), b.closest('body').tagName, document.body.children.length, a.nodeType, b.firstChild.nodeName];
		</script>");
		Assert.Equal("y z|3|v|color: red; margin-top: 10px;|1|true|New|<div id=\"a\" class=\"y z\" data-count=\"3\" data-other=\"v\" style=\"color: red; margin-top: 10px;\">hello</div>|bold text|true|BODY|3|1|B", Eval(h, "result.join('|')"));
		Assert.Equal("New", h.Host.Document.Title);
		Assert.True(h.Host.Dirty);
		Assert.True(h.Host.TitleChanged);
		Assert.Empty(h.Log);
	}

	[Fact]
	public void DispatchesEvents()
	{
		Harness h = Open(@"<div id=outer><button id=btn onclick=""window.inline = (window.inline || 0) + 1"">Go</button></div><script>
			window.order = [];
			const outer = document.getElementById('outer');
			const btn = document.getElementById('btn');
			outer.addEventListener('click', e => order.push('capture:' + e.eventPhase), true);
			outer.addEventListener('click', function (e) { order.push('bubble:' + (this === outer) + ':' + (e.target === btn) + ':' + e.currentTarget.id) });
			btn.addEventListener('click', e => { order.push('target:' + e.clientX); }, { once: true });
			document.addEventListener('DOMContentLoaded', () => order.push('ready:' + document.readyState));
			document.addEventListener('ping', e => order.push('ping:' + e.detail + ':' + e.bubbles));
			btn.dispatchEvent(new CustomEvent('ping', { detail: 42, bubbles: true }));
		</script>");
		Assert.True(h.Host.DispatchMouse("click", h.Host.ElementById("btn"), 5, 7));
		Assert.Equal("ping:42:true|ready:interactive|capture:1|target:5|bubble:true:true:outer", Eval(h, "order.join('|')"));
		Assert.Equal("1", Eval(h, "String(window.inline)"));
		Assert.True(h.Host.DispatchMouse("click", h.Host.ElementById("btn"), 5, 7));
		Assert.Equal("2", Eval(h, "String(window.inline)"));
		Assert.Equal("ping:42:true|ready:interactive|capture:1|target:5|bubble:true:true:outer|capture:1|bubble:true:true:outer", Eval(h, "order.join('|')"));
		Eval(h, "btn.onclick = e => { e.preventDefault(); e.stopPropagation(); order.length = 0 }");
		Assert.False(h.Host.DispatchMouse("click", h.Host.ElementById("btn"), 5, 7));
		Assert.Equal("", Eval(h, "order.join('|')"));
		Eval(h, "window.addEventListener('load', () => order.push('load:' + document.readyState)); window.onresize = () => order.push('resized')");
		h.Host.FireLoad();
		h.Host.DispatchSimple("resize", h.Host.Interpreter.Global, false, false);
		Assert.Equal("load:complete|resized", Eval(h, "order.join('|')"));
		Assert.Empty(h.Log);
		Assert.True(h.Host.DispatchKey("keydown", h.Host.ElementById("btn"), "Enter", "Enter", 13, false, false, false));
		Eval(h, "btn.addEventListener('keydown', e => { if (e.key === 'Enter') e.preventDefault() })");
		Assert.False(h.Host.DispatchKey("keydown", h.Host.ElementById("btn"), "Enter", "Enter", 13, false, false, false));
	}

	[Fact]
	public void HandlesForms()
	{
		Harness h = Open(@"<form id=f action=/go method=post><input name=q value=a><input type=checkbox name=c checked><select name=s><option value=1>One<option value=2 selected>Two</select><button id=go>Go</button></form><script>
			const f = document.getElementById('f');
			f.elements[0].value = 'b';
			f.querySelector('[name=c]').checked = false;
			f.querySelector('select').value = '1';
			window.block = false;
			f.addEventListener('submit', e => { window.submitted = new FormData(f).get('q') + ':' + e.submitter.id; if (window.block) e.preventDefault() });
			window.probe = [f.method, f.action, f.querySelector('select').selectedIndex, f.querySelector('select').options.length, f.elements.length];
		</script>");
		Assert.Empty(h.Log);
		Assert.Equal("post|https://example.test/go|0|2|4", Eval(h, "probe.join('|')"));
		Assert.True(h.Host.SubmitForm(h.Host.ElementById("go")));
		Assert.Equal("b:go", Eval(h, "submitted"));
		Assert.Single(h.Navigations);
		Assert.Equal("POST", h.Navigations[0].Method);
		Assert.Equal("q=b&s=1", Encoding.UTF8.GetString(h.Navigations[0].Body));
		Eval(h, "window.block = true");
		Assert.False(h.Host.SubmitForm(h.Host.ElementById("go")));
		Assert.Single(h.Navigations);
		Eval(h, "document.querySelector('[name=c]').addEventListener('change', e => window.changed = e.target.checked)");
		h.Host.Click(h.Host.ElementById("f").FirstElement("input[type=checkbox]") ?? h.Host.QueryFirst(h.Host.Document, "[name=c]"));
		Assert.Equal("true", Eval(h, "String(changed)"));
		Assert.True(h.Host.QueryFirst(h.Host.Document, "[name=c]").HasAttribute("checked"));
	}

	[Fact]
	public void RunsTimersAndObservers()
	{
		Harness h = Open(@"<ul id=list></ul><script>
			window.seen = [];
			const list = document.getElementById('list');
			new MutationObserver(records => records.forEach(r => seen.push(r.type + ':' + r.addedNodes.length + ':' + r.target.id))).observe(list, { childList: true, subtree: true, attributes: true });
			new IntersectionObserver(entries => seen.push('visible:' + entries[0].isIntersecting)).observe(list);
			setTimeout(() => { const li = document.createElement('li'); li.textContent = 'late'; list.appendChild(li); list.setAttribute('data-done', '1') }, 10);
			const id = setInterval(() => seen.push('tick'), 5);
			setTimeout(() => clearInterval(id), 12);
		</script>");
		Assert.Equal("visible:true", Eval(h, "seen.join('|')"));
		Assert.False(h.Host.Dirty);
		h.Now = 11;
		Assert.True(h.Host.Pump());
		Assert.Equal("visible:true|tick|childList:1:list|attributes:0:list|tick", Eval(h, "seen.join('|')"));
		Assert.Equal("late", h.Host.ElementById("list").TextContent);
		h.Now = 100;
		Assert.False(h.Host.Pump());
		Assert.Equal("visible:true|tick|childList:1:list|attributes:0:list|tick", Eval(h, "seen.join('|')"));
		Assert.False(h.Host.HasTimers);
	}

	[Fact]
	public void FetchesAndStores()
	{
		List<string> requests = new List<string>();
		ResourceFetcher fetch = (method, url, body, type, headers) =>
		{
			requests.Add(method + " " + url + (body == null ? "" : " " + Encoding.UTF8.GetString(body)));
			ResourceResponse response = new ResourceResponse { Status = 200, Reason = "OK", Url = url };
			response.Headers["Content-Type"] = "application/json";
			response.Body = Encoding.UTF8.GetBytes("{\"ok\": true, \"path\": \"" + url.AbsolutePath + "\"}");
			return response;
		};
		CookieJar.Clear();
		Harness h = Open(@"<script>
			window.out = [];
			window.fetched = [];
			fetch('../api/items', { method: 'POST', body: JSON.stringify({ a: 1 }), headers: { 'Content-Type': 'application/json' } }).then(r => { fetched.push(r.status + ':' + r.headers.get('content-type')); return r.json() }).then(j => fetched.push(j.path));
			const xhr = new XMLHttpRequest();
			xhr.open('GET', '/sync', false);
			xhr.send();
			out.push(xhr.readyState + ':' + xhr.status + ':' + JSON.parse(xhr.responseText).ok);
			const async = new XMLHttpRequest();
			async.onload = () => window.asyncResult = async.response.path;
			out.push('sent:' + async.readyState);
			async.responseType = 'json';
			async.open('GET', '/async');
			async.send();
			localStorage.setItem('k', 'v');
			localStorage.other = 2;
			sessionStorage.setItem('s', '1');
			out.push(localStorage.getItem('k') + localStorage.other + localStorage.length + ':' + sessionStorage.length);
			history.pushState({ p: 1 }, '', '/pushed?y=2');
			out.push(location.pathname + location.search + ':' + history.state.p + ':' + location.origin);
			const u = new URL('../a/b?c=1&d=x%20y#frag', 'https://host.test:8443/one/two/');
			u.searchParams.append('e', 'f g');
			out.push([u.href, u.host, u.pathname, u.searchParams.get('d'), u.hash, [...new URLSearchParams('a=1&a=2&b=3').getAll('a')].join('+')].join(','));
			document.cookie = 'name=value; path=/';
			document.cookie = 'other=1';
			out.push(document.cookie);
			out.push(btoa('hi') + ':' + atob('aGk='));
		</script>", fetch);
		Assert.Empty(h.Log);
		Assert.Equal("4:200:true|sent:0|v22:1|/pushed?y=2:1:https://example.test|https://host.test:8443/one/a/b?c=1&d=x+y&e=f+g#frag,host.test:8443,/one/a/b,x y,#frag,1+2|name=value; other=1|aGk=:hi", Eval(h, "out.join('|')"));
		Assert.Equal("200:application/json|/api/items", Eval(h, "fetched.join('|')"));
		Assert.Equal("/async", Eval(h, "asyncResult"));
		Assert.Equal(3, requests.Count);
		Assert.Equal("GET https://example.test/sync", requests[0]);
		Assert.Equal("POST https://example.test/api/items {\"a\":1}", requests[1]);
		Assert.Equal("GET https://example.test/async", requests[2]);
	}

	[Fact]
	public void ParsesFragmentsAndWritesMarkup()
	{
		Harness h = Open(@"<table id=t><tbody id=tb></tbody></table><template id=tpl><span class=tag>T</span></template><div id=d><i>x</i></div><script>
			const tb = document.getElementById('tb');
			tb.innerHTML = '<tr><td>a</td><td>b</td></tr>';
			const clone = document.getElementById('tpl').content.cloneNode(true);
			document.getElementById('d').appendChild(clone);
			document.getElementById('d').insertAdjacentHTML('beforebegin', '<p id=before>p</p>');
			document.write('<em id=written>w</em>');
			const copy = document.getElementById('d').cloneNode(true);
			copy.id = 'copy';
			document.body.append(copy, 'tail');
			document.querySelector('#d i').replaceWith('plain');
			document.getElementById('before').outerHTML = '<p id=after>q</p>';
			const frag = document.createDocumentFragment();
			frag.appendChild(document.createTextNode('f1'));
			frag.appendChild(document.createElement('br'));
			copy.prepend(frag);
			window.out = [tb.rows.length, tb.querySelectorAll('td').length, document.querySelectorAll('.tag').length, document.getElementById('d').innerHTML, document.getElementById('written').previousSibling.nodeName, document.getElementById('after').nextElementSibling.id, copy.innerHTML, document.body.lastChild.nodeValue, new DOMParser().parseFromString('<p>z</p>', 'text/html').body.firstChild.textContent, frag.childNodes.length];
		</script>");
		Assert.Empty(h.Log);
		Assert.Equal("1|2|2|plain<span class=\"tag\">T</span>|SCRIPT|d|f1<br><i>x</i><span class=\"tag\">T</span>|tail|z|0", Eval(h, "out.join('|')"));
	}

	[Fact]
	public void ReportsGeometryAndComputedStyle()
	{
		Harness h = Open(@"<style>#box { width: 100px; height: 50px; margin-left: 20px; background: rgb(1, 2, 3) } #gone { display: none }</style><div id=box>x</div><div id=gone>y</div><script>
			const box = document.getElementById('box');
			const rect = box.getBoundingClientRect();
			const cs = getComputedStyle(box);
			window.out = [box.offsetWidth, box.offsetHeight, rect.left, rect.width, cs.width, cs.display, cs.getPropertyValue('background-color'), cs.marginLeft, getComputedStyle(document.getElementById('gone')).display, document.documentElement.clientWidth, innerWidth, box.clientHeight];
		</script>");
		h.Host.ViewportWidth = 800;
		Assert.Empty(h.Log);
		Assert.Equal("100|50|20|100|100px|block|rgb(1, 2, 3)|20px|none|800|1200|50", Eval(h, "out.join('|')"));
		Eval(h, "box.style.width = '300px'");
		Assert.True(h.Host.Dirty);
		Assert.Equal("300", Eval(h, "String(box.offsetWidth)"));
		Assert.False(h.Host.Dirty);
	}

	[Fact]
	public void KeepsGoingAfterErrors()
	{
		Harness h = Open(@"<script>window.a = 1; undefinedFunction();</script><script>window.b = 2; while (true) {}</script><script type=module>window.c = 3</script><script type=application/json>{""not"": ""code""}</script><script>window.d = 4; document.body.appendChild(Object.assign(document.createElement('script'), { textContent: 'window.e = 5' }));</script>", run: false);
		h.Host.ScriptBudgetMs = 50;
		h.Host.Interpreter.Clock = () => Environment.TickCount64;
		h.Host.RunScripts();
		Assert.Equal("1,2,3,4,5", Eval(h, "[window.a, window.b, window.c, window.d, window.e].join()"));
		Assert.Equal(2, h.Host.ScriptErrors);
		Assert.Equal(5, h.Host.ScriptsRun);
		Assert.Contains(h.Log, line => line.Contains("undefinedFunction is not defined"));
		Assert.Contains(h.Log, line => line.Contains("Script ran too long"));
	}

	[Fact]
	public void FollowsLinksAndHashes()
	{
		Harness h = Open(@"<a id=link href=next.html>n</a><a id=hash href=#sec>s</a><h2 id=sec>S</h2><script>
			window.hashes = [];
			window.addEventListener('hashchange', e => hashes.push(location.hash + ':' + e.newURL));
			document.getElementById('link').addEventListener('click', e => window.clicked = e.target.href);
		</script>");
		h.Host.Click(h.Host.ElementById("link"));
		Assert.Equal("https://example.test/dir/next.html", Eval(h, "clicked"));
		Assert.Single(h.Navigations);
		Assert.Equal("https://example.test/dir/next.html", h.Navigations[0].Url.ToString());
		h.Host.Click(h.Host.ElementById("hash"));
		Assert.Single(h.Navigations);
		Assert.Equal("#sec:https://example.test/dir/page.html?x=1#sec", Eval(h, "hashes.join('|')"));
		Eval(h, "location.href = 'https://example.test/other'");
		Assert.Equal(2, h.Navigations.Count);
		Eval(h, "location.hash = 'two'");
		Assert.Equal(2, h.Navigations.Count);
		Assert.Equal("#two", Eval(h, "location.hash"));
	}

	[Fact]
	public void UpgradesAndBuildsCustomElements()
	{
		Harness h = Open(@"<x-greet name=early></x-greet><div id=slot></div><script>
			window.log = [];
			class Greet extends HTMLElement {
				static get observedAttributes() { return ['name'] }
				constructor() { super(); log.push('ctor:' + this.tagName) }
				connectedCallback() { this.textContent = 'hello ' + (this.getAttribute('name') || 'world'); log.push('connected') }
				disconnectedCallback() { log.push('disconnected') }
				attributeChangedCallback(n, o, v) { log.push('attr:' + n + ':' + o + ':' + v) }
				shout() { return this.textContent.toUpperCase() }
			}
			customElements.define('x-greet', Greet);
			const late = document.createElement('x-greet');
			late.setAttribute('name', 'late');
			document.getElementById('slot').appendChild(late);
			late.remove();
			window.out = [document.querySelector('x-greet').shout(), late instanceof Greet, late instanceof HTMLElement, late.shout(), customElements.get('x-greet') === Greet];
		</script>");
		Assert.Empty(h.Log);
		Assert.Equal("HELLO EARLY|true|true|HELLO LATE|true", Eval(h, "out.join('|')"));
		Assert.Equal("ctor:X-GREET|connected|ctor:X-GREET|attr:name:null:late|connected|disconnected", Eval(h, "log.join('|')"));
	}

	[Fact]
	public void RunsModuleScriptsWithAnImportMap()
	{
		ResourceFetcher fetch = (method, url, body, type, headers) =>
		{
			string source = url.AbsolutePath switch
			{
				"/js/greet.js" => "export const greet = n => 'hi ' + n; export default 'dflt';",
				"/dir/local.js" => "import { greet } from 'lib/greet.js'; export const text = greet('local');",
				_ => null,
			};
			return source == null ? new ResourceResponse { Status = 404, Url = url } : new ResourceResponse { Status = 200, Url = url, Body = Encoding.UTF8.GetBytes(source) };
		};
		Harness h = Open(@"<script type=importmap>{ ""imports"": { ""lib/"": ""/js/"" } }</script>
			<script nomodule>window.legacy = true</script>
			<script>window.order = ['classic']</script>
			<script type=module>import { text } from './local.js'; import d from 'lib/greet.js'; order.push('module:' + text + ':' + d); document.title = text;</script>
			<script type=module src=/dir/local.js></script>
			<script>order.push('classic2')</script>", fetch);
		Assert.Empty(h.Log);
		Assert.Equal("classic|classic2|module:hi local:dflt", Eval(h, "order.join('|')"));
		Assert.Equal("hi local", h.Host.Document.Title);
		Assert.Equal("undefined", Eval(h, "typeof window.legacy"));
	}

	[Fact]
	public void RendersShadowTreesWithSlots()
	{
		Harness h = Open(@"<x-card id=card><span slot=title>T</span>light</x-card><script>
			class Card extends HTMLElement {
				constructor() { super(); const r = this.attachShadow({ mode: 'open' }); r.innerHTML = '<style>b { color: red }</style><b id=inner>[</b><slot name=title></slot><i id=close>]</i><slot></slot>' }
			}
			customElements.define('x-card', Card);
			const card = document.getElementById('card');
			window.out = [card.shadowRoot.querySelector('#inner').textContent, card.shadowRoot.host === card, document.getElementById('inner'), card.shadowRoot.getRootNode() === card.shadowRoot, card.shadowRoot.querySelector('#inner').getRootNode() === card.shadowRoot, card.querySelector('span').assignedSlot.name, card.shadowRoot.querySelector('slot[name=title]').assignedNodes().length, card.shadowRoot.querySelector('slot:not([name])').assignedNodes().length, card.childNodes.length, card.shadowRoot.nodeType, card.shadowRoot instanceof ShadowRoot];
		</script>");
		Assert.Empty(h.Log);
		Assert.Equal("[|true||true|true|title|1|1|2|11|true", Eval(h, "out.join('|')"));
		h.Host.EnsureLayout();
		Element card = h.Host.ElementById("card");
		Assert.NotNull(Find(h.Root, card.ShadowRoot.FirstElement("i")));
		Assert.NotNull(Find(h.Root, card.FirstElement("span")));
		LayoutBox inner = Find(h.Root, card.ShadowRoot.FirstElement("b"));
		Assert.NotNull(inner);
		Assert.Equal("rgb(255, 0, 0)", h.Host.ComputedValue(card.ShadowRoot.FirstElement("b"), "color"));
	}

	[Fact]
	public void ExposesWindowSurface()
	{
		Harness h = Open(@"<script>
			window.out = [typeof window.fetch, typeof requestAnimationFrame, navigator.userAgent.includes('Alloy'), navigator.language, typeof matchMedia('(min-width: 500px)').matches, matchMedia('(min-width: 500px)').matches, screen.width > 0, typeof performance.now(), typeof crypto.randomUUID(), new Intl.NumberFormat('en-US').format(1234.5), new Intl.DateTimeFormat().resolvedOptions().timeZone, CSS.supports('display', 'grid'), document instanceof Document, document.body instanceof HTMLElement, document.createElement('a') instanceof HTMLAnchorElement, Object.prototype.toString.call(document.body), typeof customElements.define, 'serviceWorker' in navigator, typeof Worker, new Headers({ 'X-A': '1' }).get('x-a'), String(new TextDecoder().decode(new TextEncoder().encode('ok')))];
		</script>");
		Assert.Empty(h.Log);
		Assert.Equal("function|function|true|en-US|boolean|true|true|number|string|1,234.5|UTC|true|true|true|true|[object HTMLBodyElement]|function|false|undefined|1|ok", Eval(h, "out.join('|')"));
	}

	[Fact]
	public void RequestHeadersReachTheNetworkAndStyleKnowsItsProperties()
	{
		List<string> seen = new List<string>();
		ResourceFetcher fetch = (method, url, body, type, headers) =>
		{
			seen.Add(method + " " + url.AbsolutePath + " " + string.Join(";", headers == null ? new List<string>() : new List<KeyValuePair<string, string>>(headers).ConvertAll(h => h.Key.ToLowerInvariant() + "=" + h.Value)));
			return new ResourceResponse { Status = 200, Url = url, Body = System.Text.Encoding.UTF8.GetBytes("{}") };
		};
		Harness harness = Open("<div id=\"d\"></div><script>const x = new XMLHttpRequest(); x.open('GET', '/geo', false); x.setRequestHeader('Accept', 'application/json'); x.send(); fetch('/api', { headers: { 'X-Token': 'abc' } }); const s = document.getElementById('d').style; console.log(('transform' in s) + ' ' + ('webkitPerspective' in s) + ' ' + ('bogusThing' in s) + ' ' + ('cssText' in s))</script>", fetch);
		harness.Host.Pump();
		Assert.Contains("GET /geo accept=application/json", seen);
		Assert.Contains("GET /api x-token=abc", seen);
		Assert.Contains("true true false true", harness.Log);
	}

	[Fact]
	public void CanvasHasAQuiet2dContext()
	{
		Harness harness = Open("<canvas id=\"c\" width=\"10\" height=\"10\"></canvas><script>const c = document.getElementById('c'); const ctx = c.getContext('2d'); ctx.font = '20px sans-serif'; ctx.fillRect(0, 0, 5, 5); const m = ctx.measureText('abcd'); const d = ctx.getImageData(0, 0, 2, 2); console.log((ctx.canvas === c) + ' ' + (m.width > 20) + ' ' + d.data.length + ' ' + (c.getContext('webgl') === null) + ' ' + (c.getContext('2d') === ctx))</script>");
		Assert.Contains("true true 16 true true", harness.Log);
	}

	[Fact]
	public void ForcedLayoutsCountAgainstTheBudget()
	{
		Harness harness = Open("<div id=\"d\">x</div><script>const d = document.getElementById('d'); let n = 0; try { for (let i = 0; i < 50; i++) { d.style.width = (100 + i) + 'px'; n += d.offsetWidth > 0 ? 1 : 0 } } catch (e) { console.log(e.name + ' after ' + n) }</script>", null, false);
		LayoutRequest layout = harness.Host.EnsureLayout;
		harness.Host.EnsureLayout = () =>
		{
			harness.Now += 100;
			layout();
		};
		harness.Host.ScriptBudgetMs = 350;
		harness.Host.RunScripts();
		Assert.Contains(harness.Log, line => line.StartsWith("RangeError after"));
		Assert.InRange(harness.Host.ForcedLayouts, 1, 10);
	}
}
