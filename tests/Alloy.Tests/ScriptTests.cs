using System.Collections.Generic;
using Alloy.Sdk.Script;

namespace Alloy.Tests;

// The JavaScript engine: each test runs a script and checks what it computes or logs
public class ScriptTests
{
	private static Interpreter New(List<string> log = null)
	{
		Interpreter interpreter = new Interpreter();
		interpreter.OnConsole = (level, text) => log?.Add(text);
		interpreter.OnError = text => log?.Add(text);
		return interpreter;
	}

	private static string Eval(string source)
	{
		Interpreter interpreter = New();
		return interpreter.Evaluate(source).ToJsString();
	}

	private static List<string> Log(string source)
	{
		List<string> log = new List<string>();
		Interpreter interpreter = New(log);
		interpreter.TryEvaluate(source);
		return log;
	}

	[Fact]
	public void EvaluatesArithmeticAndCoercion()
	{
		Assert.Equal("7", Eval("1 + 2 * 3"));
		Assert.Equal("12", Eval("'1' + 2"));
		Assert.Equal("-1", Eval("'1' - 2"));
		Assert.Equal("true", Eval("1 == '1' && 1 !== '1' && null == undefined && null !== undefined"));
		Assert.Equal("NaN", Eval("'a' * 2"));
		Assert.Equal("8", Eval("2 ** 3"));
		Assert.Equal("1", Eval("7 % 3"));
		Assert.Equal("-2", Eval("~1"));
		Assert.Equal("1073741823", Eval("-1 >>> 2"));
		Assert.Equal("0.30000000000000004", Eval("0.1 + 0.2"));
		Assert.Equal("1e+21", Eval("1e21"));
		Assert.Equal("true", Eval("typeof x === 'undefined' && typeof null === 'object' && typeof (() => 1) === 'function'"));
		Assert.Equal("5", Eval("let a = 1; a += 4; a"));
		Assert.Equal("ab", Eval("null ?? 'a' + (undefined ?? 'b')"));
		Assert.Equal("3", Eval("var o = {x: 3}; o?.x ?? 0"));
		Assert.Equal("undefined", Eval("var o = null; o?.x.y.z"));
	}

	[Fact]
	public void RunsControlFlow()
	{
		Assert.Equal("55", Eval("let s = 0; for (let i = 1; i <= 10; i++) s += i; s"));
		Assert.Equal("acd", Eval("let r = ''; for (const c of 'abcd') { if (c === 'b') continue; r += c } r"));
		Assert.Equal("a,b", Eval("const o = {a: 1, b: 2}; const k = []; for (const key in o) k.push(key); k.join()"));
		Assert.Equal("3", Eval("let i = 0; while (true) { if (++i === 3) break } i"));
		Assert.Equal("1", Eval("let i = 0; do { i++ } while (false); i"));
		Assert.Equal("two", Eval("function f(n) { switch (n) { case 1: return 'one'; case 2: return 'two'; default: return 'many' } } f(2)"));
		Assert.Equal("fall", Eval("let r = ''; switch (1) { case 1: r += 'f'; case 2: r += 'all'; break; case 3: r += 'no' } r"));
		Assert.Equal("4", Eval("let n = 0; outer: for (let i = 0; i < 3; i++) { for (let j = 0; j < 3; j++) { if (j === 2) continue outer; if (i === 2) break outer; n++ } } n"));
		Assert.Equal("6", Eval("const [a, , b = 4, ...rest] = [1, 2, undefined, 5, 6]; a + b + rest.length - 1"));
		Assert.Equal("1-2-3", Eval("const {x, y: [z], w = 3} = {x: 1, y: [2]}; `${x}-${z}-${w}`"));
		Assert.Equal("2,1", Eval("let a = 1, b = 2; [a, b] = [b, a]; [a, b].join()"));
		Assert.Equal("5,6", Eval("let a, b; ({a, b = 6} = {a: 5}); [a, b].join()"));
		Assert.Equal("0,1,1,2,3,5,8,13", Eval("function* fib() { let [a, b] = [0, 1]; while (true) { yield a; [a, b] = [b, a + b] } } const f = []; for (const n of fib()) { if (f.length === 8) break; f.push(n) } f.join()"));
		Assert.Equal("a=1;b=2", Eval("const r = []; let k, v; for ([k, v] of Object.entries({a: 1, b: 2})) r.push(k + '=' + v); r.join(';')"));
		Assert.Equal("3", Eval("const o = {}; [o.x, o['y']] = [1, 2]; o.x + o.y"));
	}

	[Fact]
	public void HandlesFunctionsClosuresAndThis()
	{
		Assert.Equal("120", Eval("function fact(n) { return n <= 1 ? 1 : n * fact(n - 1) } fact(5)"));
		Assert.Equal("3", Eval("function counter() { let c = 0; return () => ++c } const c = counter(); c(); c(); c()"));
		Assert.Equal("0,1,2", Eval("const fs = []; for (let i = 0; i < 3; i++) fs.push(() => i); fs.map(f => f()).join()"));
		Assert.Equal("3,3,3", Eval("var fs = []; for (var i = 0; i < 3; i++) fs.push(() => i); fs.map(f => f()).join()"));
		Assert.Equal("10", Eval("const o = { v: 10, get() { return this.v }, arrow: () => typeof this }; o.get()"));
		Assert.Equal("7", Eval("function f(a, b = a + 1, ...rest) { return a + b + rest.length } f(3)"));
		Assert.Equal("3", Eval("function f() { return arguments.length } f(1, 2, 3)"));
		Assert.Equal("ab", Eval("function f(x) { return this.p + x } f.call({p: 'a'}, 'b')"));
		Assert.Equal("5", Eval("const add = (a, b) => a + b; add.apply(null, [2, 3])"));
		Assert.Equal("hi bob", Eval("function greet(g, n) { return g + ' ' + n } const hi = greet.bind(null, 'hi'); hi('bob')"));
		Assert.Equal("6", Eval("const f = function g(n) { return n ? n + g(n - 1) : 0 }; f(3)"));
		Assert.Equal("true", Eval("hoisted(); function hoisted() { return true }"));
		Assert.Equal("inner", Eval("var x = 'outer'; function f() { var x = 'inner'; return x } f()"));
		Assert.Equal("ok", Eval("(function() { try { undeclared } catch (e) { return e instanceof ReferenceError ? 'ok' : e.name } })()"));
		Assert.Equal("f", Eval("const f = () => 1; f.name"));
		Assert.Equal("2", Eval("const f = function(a, b) {}; f.length"));
		Assert.Equal("object", Eval("typeof new (function F() { this.a = 1 })()"));
		Assert.Equal("1", Eval("function F() { this.a = 1 } F.prototype.get = function() { return this.a }; new F().get()"));
	}

	[Fact]
	public void SupportsClassesAndInheritance()
	{
		string source = @"
			class Animal {
				static count = 0;
				#secret = 'hidden';
				constructor(name) { this.name = name; Animal.count++ }
				speak() { return `${this.name} makes a sound` }
				get upper() { return this.name.toUpperCase() }
				set upper(v) { this.name = v.toLowerCase() }
				reveal() { return this.#secret }
				static create(n) { return new this(n) }
			}
			class Dog extends Animal {
				constructor(name) { super(name); this.kind = 'dog' }
				speak() { return super.speak() + ' (woof)' }
				toString() { return 'Dog:' + this.name }
			}
			const d = Dog.create('Rex');
			d.upper = 'FIDO';
			[d.speak(), d.upper, d.reveal(), Animal.count, d instanceof Animal, d instanceof Dog, String(d), d.kind, Object.getPrototypeOf(Dog) === Animal, d.constructor.name].join('|')";
		Assert.Equal("fido makes a sound (woof)|FIDO|hidden|1|true|true|Dog:fido|dog|true|Dog", Eval(source));
		Assert.Equal("TypeError", Eval("class A {} try { A() } catch (e) { e.name }"));
		Assert.Equal("custom:boom", Eval("class MyError extends Error { constructor(m) { super(m); this.name = 'custom' } } try { throw new MyError('boom') } catch (e) { e.name + ':' + e.message + (e instanceof Error ? '' : '!') }"));
		Assert.Equal("3", Eval("class Stack extends Array { top() { return this[this.length - 1] } } const s = new Stack(); s.push(1, 2, 3); s.top()"));
		Assert.Equal("field", Eval("class A { x = 'field'; y = this.x } new A().y"));
		Assert.Equal("1,2", Eval("class A { static [Symbol.iterator]() {} static *gen() { yield 1; yield 2 } } [...A.gen()].join()"));
	}

	[Fact]
	public void ThrowsAndCatches()
	{
		Assert.Equal("caught 1", Eval("try { throw 1 } catch (e) { 'caught ' + e }"));
		Assert.Equal("a,f,b", Eval("const r = []; function f() { try { r.push('a'); return 'x' } finally { r.push('f') } } f(); r.push('b'); r.join()"));
		Assert.Equal("finally", Eval("function f() { try { throw 1 } catch { return 'catch' } finally { return 'finally' } } f()"));
		Assert.Equal("TypeError", Eval("try { null.x } catch (e) { e.constructor.name }"));
		Assert.Equal("Cannot read properties of undefined (reading 'foo')", Eval("try { undefined.foo } catch (e) { e.message }"));
		Assert.Equal("x is not a function", Eval("let x = 1; try { x() } catch (e) { e.message }"));
		Assert.Equal("RangeError", Eval("function r() { r() } try { r() } catch (e) { e.name }"));
		Assert.Equal("true", Eval("const e = new Error('m', { cause: 1 }); e.cause === 1 && e.stack.includes('Error: m') && e.toString() === 'Error: m'"));
		Assert.Equal("Assignment to constant variable.", Eval("const c = 1; try { c = 2 } catch (e) { e.message }"));
		Assert.Equal("ReferenceError", Eval("try { tdz; let tdz = 1 } catch (e) { e.name }"));
		List<string> log = Log("console.log('before'); throw new TypeError('oops'); console.log('after')");
		Assert.Equal("before", log[0]);
		Assert.StartsWith("Uncaught TypeError: oops", log[1]);
		Assert.Equal(2, log.Count);
	}

	[Fact]
	public void ProvidesObjectBuiltins()
	{
		Assert.Equal("a,b", Eval("Object.keys({a: 1, b: 2}).join()"));
		Assert.Equal("1,2", Eval("Object.values({a: 1, b: 2}).join()"));
		Assert.Equal("a=1;b=2", Eval("Object.entries({a: 1, b: 2}).map(([k, v]) => k + '=' + v).join(';')"));
		Assert.Equal("3", Eval("Object.assign({}, {a: 1}, {b: 2}).a + Object.assign({}, {a: 1}, {b: 2}).b"));
		Assert.Equal("1,2,3", Eval("const a = {x: 1}; const b = {...a, y: 2, z: 3}; Object.values(b).join()"));
		Assert.Equal("1,3", Eval("const {y, ...rest} = {x: 1, y: 2, z: 3}; Object.values(rest).join()"));
		Assert.Equal("true", Eval("const o = Object.freeze({a: 1}); o.a = 2; o.b = 3; o.a === 1 && o.b === undefined && Object.isFrozen(o)"));
		Assert.Equal("1", Eval("const o = {}; Object.defineProperty(o, 'x', { get() { return 1 }, enumerable: false }); o.x + Object.keys(o).length"));
		Assert.Equal("true", Eval("const p = {greet() { return 'hi' }}; const o = Object.create(p); o.greet() === 'hi' && Object.getPrototypeOf(o) === p && !o.hasOwnProperty('greet') && 'greet' in o"));
		Assert.Equal("[object Array]", Eval("Object.prototype.toString.call([])"));
		Assert.Equal("1,2,10", Eval("const o = {10: 'c', 2: 'b', 1: 'a'}; Object.keys(o).join()"));
		Assert.Equal("true", Eval("const s = Symbol('tag'); const o = {[s]: 1, a: 2}; o[s] === 1 && Object.keys(o).length === 1 && typeof s === 'symbol' && s.toString() === 'Symbol(tag)'"));
		Assert.Equal("42", Eval("const o = { valueOf() { return 42 } }; o + 0"));
		Assert.Equal("x", Eval("const o = { toString() { return 'x' } }; `${o}`"));
		Assert.Equal("true", Eval("const o = {a: {b: 1}}; const c = structuredClone(o); c.a !== o.a && c.a.b === 1"));
		Assert.Equal("0", Eval("let n = 0; const o = { set v(x) { n = x } }; o.v = 5; n - 5"));
		Assert.Equal("yes", Eval("const o = {}; o.__proto__ = { k: 'yes' }; o.k"));
		Assert.Equal("3", Eval("const o = { __proto__: { a: 3 } }; o.a"));
	}

	[Fact]
	public void ProvidesArrayBuiltins()
	{
		Assert.Equal("2,4,6", Eval("[1, 2, 3].map(x => x * 2).join()"));
		Assert.Equal("6", Eval("[1, 2, 3].reduce((a, b) => a + b, 0)"));
		Assert.Equal("2,3", Eval("[1, 2, 3].filter(x => x > 1).join()"));
		Assert.Equal("1,2,3,4", Eval("[3, 1, 4, 2].sort().join()"));
		Assert.Equal("4,3,2,1", Eval("[3, 1, 4, 2].sort((a, b) => b - a).join()"));
		Assert.Equal("1,2,10", Eval("[10, 1, 2].sort((a, b) => a - b).join()"));
		Assert.Equal("1,10,2", Eval("[10, 1, 2].sort().join()"));
		Assert.Equal("b", Eval("[{n: 'a', v: 2}, {n: 'b', v: 1}].sort((x, y) => x.v - y.v)[0].n"));
		Assert.Equal("true", Eval("[1, 2, 3].includes(2) && [1, 2, 3].indexOf(3) === 2 && [NaN].includes(NaN) && [NaN].indexOf(NaN) === -1"));
		Assert.Equal("2", Eval("[1, 2, 3].find(x => x > 1)"));
		Assert.Equal("1,2,3,4", Eval("[1, [2, [3, [4]]]].flat(Infinity).join()"));
		Assert.Equal("3", Eval("const a = [1, 2]; a.push(3); a.length"));
		Assert.Equal("1,4,5,3", Eval("const a = [1, 2, 3]; a.splice(1, 1, 4, 5); a.join()"));
		Assert.Equal("2", Eval("const a = [1, 2, 3]; a.splice(1, 1)[0]"));
		Assert.Equal("c,b,a", Eval("['a', 'b', 'c'].reverse().join()"));
		Assert.Equal("b,c", Eval("['a', 'b', 'c'].slice(1).join()"));
		Assert.Equal("a,b", Eval("['a', 'b', 'c'].slice(0, -1).join()"));
		Assert.Equal("3", Eval("Array.from({length: 3}, (_, i) => i).length"));
		Assert.Equal("1,2,3", Eval("Array.from(new Set([1, 2, 2, 3])).join()"));
		Assert.Equal("true", Eval("Array.isArray([]) && !Array.isArray({}) && Array.of(1, 2).length === 2"));
		Assert.Equal("4", Eval("[...[1, 2], ...[3, 4]].length"));
		Assert.Equal("0:a,1:b", Eval("[...['a', 'b'].entries()].map(([i, v]) => i + ':' + v).join()"));
		Assert.Equal("3", Eval("const a = []; a[2] = 1; a.length"));
		Assert.Equal("0", Eval("const a = [1, 2, 3]; a.length = 0; a.length"));
		Assert.Equal("1,,3", Eval("[1, null, 3].join()"));
		Assert.Equal("true", Eval("[1, 2, 3].some(x => x === 2) && [1, 2, 3].every(x => x > 0) && ![].some(x => true)"));
		Assert.Equal("3", Eval("[1, 2, 3].at(-1)"));
		Assert.Equal("a-b", Eval("const parts = []; ['a', 'b'].forEach((v, i) => parts[i] = v); parts.join('-')"));
		Assert.Equal("3", Eval("Math.max(...[1, 3, 2])"));
		Assert.Equal("[1,2,[3]]", Eval("JSON.stringify([1, 2, [3]])"));
		Assert.Equal("1,2,3", Eval("function f() { return Array.prototype.slice.call(arguments) } f(1, 2, 3).join()"));
		Assert.Equal("3,c,b,2,a", Eval("const o = {length: 1, 0: 'a'}; Array.prototype.push.call(o, 'b', 'c'); const r = [o.length, o[2]]; Array.prototype.splice.call(o, 0, 1); r.push(o[0], o.length, Array.prototype.pop.call({length: 2, 0: 'x', 1: 'a'})); r.join()"));
	}

	[Fact]
	public void ProvidesStringBuiltins()
	{
		Assert.Equal("HELLO", Eval("'hello'.toUpperCase()"));
		Assert.Equal("a,b,c", Eval("'a-b-c'.split('-').join()"));
		Assert.Equal("a,b,c", Eval("'a1b22c'.split(/\\d+/).join()"));
		Assert.Equal("h,e,y", Eval("'hey'.split('').join()"));
		Assert.Equal("ell", Eval("'hello'.slice(1, 4) === 'hello'.substring(1, 4) ? 'ell' : 'no'"));
		Assert.Equal("5", Eval("'hello'.length"));
		Assert.Equal("l", Eval("'hello'[2]"));
		Assert.Equal("true", Eval("'hello'.startsWith('he') && 'hello'.endsWith('lo') && 'hello'.includes('ell') && 'hello'.indexOf('l') === 2 && 'hello'.lastIndexOf('l') === 3"));
		Assert.Equal("hi", Eval("'  hi  '.trim()"));
		Assert.Equal("007", Eval("'7'.padStart(3, '0')"));
		Assert.Equal("ababab", Eval("'ab'.repeat(3)"));
		Assert.Equal("hallo", Eval("'hello'.replace('e', 'a')"));
		Assert.Equal("h-ll-", Eval("'hello'.replace(/[eo]/g, '-')"));
		Assert.Equal("h-ll-", Eval("'hello'.replaceAll(/[eo]/g, '-')"));
		Assert.Equal("a.b.c", Eval("'a-b-c'.replaceAll('-', '.')"));
		Assert.Equal("[e][o]", Eval("'eo'.replace(/(e)(o)/, '[$1][$2]')"));
		Assert.Equal("HELLO world", Eval("'hello world'.replace(/\\w+/, m => m.toUpperCase())"));
		Assert.Equal("2024-01-15", Eval("'15/01/2024'.replace(/(?<d>\\d+)\\/(?<m>\\d+)\\/(?<y>\\d+)/, '$<y>-$<m>-$<d>')"));
		Assert.Equal("1,2,3", Eval("'a1b2c3'.match(/\\d/g).join()"));
		Assert.Equal("b2", Eval("const m = 'a1b2'.match(/([a-z])(\\d)/g); m[1]"));
		Assert.Equal("1", Eval("const m = /(\\w)(\\d)/.exec('a1b2'); m.index + m[2].length"));
		Assert.Equal("true", Eval("/^[\\w.]+@\\w+\\.\\w+$/.test('a.b@c.de') && !/^\\d+$/.test('12a')"));
		Assert.Equal("no", Eval("'x\\n'.match(/x$/) ? 'x' : 'no'"));
		Assert.Equal("true", Eval("/^[a-z$_][\\w-$]*$/.test('my-var$') && /[\\d.]+/.test('3.5') && !/^[\\w-]+$/.test('a b')"));
		Assert.Equal("no", Eval("'x\\ny'.match(/x$/) ? 'x' : 'no'"));
		Assert.Equal("x", Eval("'x\\ny'.match(/x$/m) ? 'x' : 'no'"));
		Assert.Equal("1:a,3:b", Eval("[...'a-b'.matchAll(/(\\w)/g)].map(m => (m.index + 1) + ':' + m[1]).join()"));
		Assert.Equal("4", Eval("'abc'.charCodeAt(0) - 93"));
		Assert.Equal("A", Eval("String.fromCharCode(65)"));
		Assert.Equal("3", Eval("String(3) + ''"));
		Assert.Equal("a b", Eval("const x = 'a'; const y = 'b'; `${x} ${y}`"));
		Assert.Equal("1|2", Eval("function tag(s, ...v) { return v.join(s[1]) } tag`a${1}|${2}b`"));
		Assert.Equal("true", Eval("'b'.localeCompare('a') === 1 && 'abc'.normalize() === 'abc'"));
		Assert.Equal("3", Eval("[...'a\\uD83D\\uDE00b'].length"));
		Assert.Equal("true", Eval("new String('x') instanceof String && typeof new String('x') === 'object' && new String('x') + '' === 'x'"));
	}

	[Fact]
	public void ProvidesNumberAndMathBuiltins()
	{
		Assert.Equal("3.14", Eval("(3.14159).toFixed(2)"));
		Assert.Equal("1.0", Eval("(1).toFixed(1)"));
		Assert.Equal("1.00", Eval("(1.005).toFixed(2)"));
		Assert.Equal("0.13", Eval("(0.125).toFixed(2)"));
		Assert.Equal("-1.4", Eval("(-1.45).toFixed(1)"));
		Assert.Equal("0", Eval("(0.4).toFixed(0)"));
		Assert.Equal("ff", Eval("(255).toString(16)"));
		Assert.Equal("1010", Eval("(10).toString(2)"));
		Assert.Equal("255", Eval("parseInt('ff', 16)"));
		Assert.Equal("12", Eval("parseInt('12px')"));
		Assert.Equal("NaN", Eval("parseInt('px')"));
		Assert.Equal("3.5", Eval("parseFloat('3.5em')"));
		Assert.Equal("255", Eval("Number('0xff')"));
		Assert.Equal("0", Eval("Number('')"));
		Assert.Equal("NaN", Eval("Number('12px')"));
		Assert.Equal("true", Eval("Number.isInteger(5) && !Number.isInteger(5.5) && Number.isNaN(NaN) && !isNaN(5) && isNaN('x') && Number.MAX_SAFE_INTEGER === 9007199254740991"));
		Assert.Equal("4", Eval("Math.round(3.5) + Math.floor(0.9) + Math.ceil(0.1) - Math.trunc(1.9)"));
		Assert.Equal("5", Eval("Math.hypot(3, 4)"));
		Assert.Equal("3", Eval("Math.max(1, 3, 2)"));
		Assert.Equal("-Infinity", Eval("Math.max()"));
		Assert.Equal("true", Eval("Math.random() < 1 && Math.abs(-2) === 2 && Math.sign(-3) === -1 && Math.PI > 3.14"));
		Assert.Equal("1e+21", Eval("String(1e21)"));
		Assert.Equal("1.5e-7", Eval("String(0.00000015)"));
		Assert.Equal("123456789012", Eval("String(123456789012)"));
		Assert.Equal("1,234.5", Eval("(1234.5).toLocaleString()"));
		Assert.Equal("1.23e+3", Eval("(1234.5).toExponential(2)"));
		Assert.Equal("1.2e+3", Eval("(1234.5).toPrecision(2)"));
		Assert.Equal("12.35", Eval("(12.345).toPrecision(4)"));
	}

	[Fact]
	public void ParsesAndWritesJson()
	{
		Assert.Equal("1", Eval("JSON.parse('{\"a\": [1, {\"b\": true}], \"c\": null}').a[0]"));
		Assert.Equal("true", Eval("JSON.parse('{\"a\": [1, {\"b\": true}]}').a[1].b"));
		Assert.Equal("héllo\n", Eval("JSON.parse('\"h\\\\u00e9llo\\\\n\"')"));
		Assert.Equal("{\"a\":1,\"b\":[true,null,\"x\"],\"c\":{\"d\":null}}", Eval("JSON.stringify({a: 1, b: [true, null, 'x'], c: {d: null}, e: undefined, f() {}})"));
		Assert.Equal("{\n  \"a\": 1,\n  \"b\": [\n    1,\n    2\n  ]\n}", Eval("JSON.stringify({a: 1, b: [1, 2]}, null, 2)"));
		Assert.Equal("\"a\\\"b\\\\\\n\"", Eval("JSON.stringify('a\"b\\\\\\n')"));
		Assert.Equal("undefined", Eval("String(JSON.stringify(undefined))"));
		Assert.Equal("{\"a\":2}", Eval("JSON.stringify({a: 1, b: 2}, (k, v) => k === 'b' ? undefined : (k === 'a' ? 2 : v))"));
		Assert.Equal("{\"a\":1}", Eval("JSON.stringify({a: 1, b: 2}, ['a'])"));
		Assert.Equal("\"custom\"", Eval("JSON.stringify({toJSON() { return 'custom' }})"));
		Assert.Equal("2", Eval("JSON.parse('{\"a\": 1}', (k, v) => typeof v === 'number' ? v * 2 : v).a"));
		Assert.Equal("SyntaxError", Eval("try { JSON.parse('{bad') } catch (e) { e.name }"));
		Assert.Equal("TypeError", Eval("const o = {}; o.self = o; try { JSON.stringify(o) } catch (e) { e.name }"));
		Assert.Equal("\"2024-01-15T10:30:00.000Z\"", Eval("JSON.stringify(new Date('2024-01-15T10:30:00Z'))"));
	}

	[Fact]
	public void ProvidesDates()
	{
		Assert.Equal("1705314600000", Eval("new Date('2024-01-15T10:30:00Z').getTime()"));
		Assert.Equal("1705314600000", Eval("Date.parse('2024-01-15T10:30:00.000Z')"));
		Assert.Equal("2024-0-15-1", Eval("const d = new Date(2024, 0, 15); [d.getFullYear(), d.getMonth(), d.getDate(), d.getDay()].join('-')"));
		Assert.Equal("2024-01-15T00:00:00.000Z", Eval("new Date(Date.UTC(2024, 0, 15)).toISOString()"));
		Assert.Equal("Mon Jan 15 2024", Eval("new Date(2024, 0, 15).toDateString()"));
		Assert.Equal("true", Eval("Date.now() > 1700000000000 && new Date().getTime() > 1700000000000"));
		Assert.Equal("Invalid Date", Eval("String(new Date('garbage'))"));
		Assert.Equal("true", Eval("isNaN(new Date('garbage').getTime())"));
		Assert.Equal("2024-03-01", Eval("const d = new Date(2024, 1, 29); d.setDate(d.getDate() + 1); d.toISOString().slice(0, 10)"));
		Assert.Equal("1000", Eval("new Date(2000) - new Date(1000)"));
		Assert.Equal("1970-01-01T00:00:00.000Z", Eval("new Date(0).toJSON()"));
		Assert.Equal("true", Eval("new Date('March 7, 2021').getMonth() === 2 && new Date('2021-03-07').getDate() === 7"));
		Assert.Equal("number", Eval("typeof (new Date() * 1)"));
	}

	[Fact]
	public void ProvidesMapsAndSets()
	{
		Assert.Equal("2", Eval("const m = new Map(); m.set('a', 1).set('b', 2); m.size"));
		Assert.Equal("1", Eval("const m = new Map([['a', 1]]); m.get('a')"));
		Assert.Equal("true", Eval("const m = new Map(); const k = {}; m.set(k, 'v'); m.get(k) === 'v' && m.get({}) === undefined && m.has(k) && m.delete(k) && !m.has(k)"));
		Assert.Equal("a=1,b=2", Eval("const m = new Map([['a', 1], ['b', 2]]); [...m].map(([k, v]) => k + '=' + v).join()"));
		Assert.Equal("a,b", Eval("const m = new Map([['a', 1], ['b', 2]]); [...m.keys()].join()"));
		Assert.Equal("3", Eval("const s = new Set([1, 2, 2, 3]); s.size"));
		Assert.Equal("true", Eval("const s = new Set(); s.add(NaN); s.add(NaN); s.add(0); s.add(-0); s.size === 2 && s.has(NaN)"));
		Assert.Equal("1,3", Eval("const s = new Set([1, 2, 3]); s.delete(2); [...s].join()"));
		Assert.Equal("v:k", Eval("let r; new Map([['k', 'v']]).forEach((v, k) => r = v + ':' + k); r"));
		Assert.Equal("true", Eval("const w = new WeakMap(); const k = {}; w.set(k, 1); w.get(k) === 1 && w.has(k)"));
		Assert.Equal("1,2,3", Eval("[...new Set([1, 2]).union(new Set([3]))].join()"));
		Assert.Equal("odd:1,3;even:2", Eval("const g = Map.groupBy([1, 2, 3], n => n % 2 ? 'odd' : 'even'); [...g].map(([k, v]) => k + ':' + v.join()).join(';')"));
	}

	[Fact]
	public void RunsGeneratorsAndIterators()
	{
		Assert.Equal("1,2,3", Eval("function* g() { yield 1; yield 2; return 3 } const it = g(); [it.next().value, it.next().value, it.next().value].join()"));
		Assert.Equal("true", Eval("function* g() { yield 1 } const it = g(); it.next(); it.next().done"));
		Assert.Equal("0,1,2,3,4", Eval("function* range(n) { for (let i = 0; i < n; i++) yield i } [...range(5)].join()"));
		Assert.Equal("1,2,3,4", Eval("function* a() { yield 1; yield* [2, 3]; yield 4 } Array.from(a()).join()"));
		Assert.Equal("5", Eval("function* g() { const x = yield 1; yield x + 1 } const it = g(); it.next(); it.next(4).value"));
		Assert.Equal("cleanup", Eval("let r = ''; function* g() { try { yield 1; yield 2 } finally { r = 'cleanup' } } for (const v of g()) break; r"));
		Assert.Equal("caught", Eval("function* g() { try { yield 1 } catch (e) { yield 'caught' } } const it = g(); it.next(); it.throw('x').value"));
		Assert.Equal("1,4,9", Eval("const it = { from: 1, to: 3, [Symbol.iterator]() { let c = this.from; const t = this.to; return { next: () => c <= t ? { value: c * c++, done: false } : { done: true } } } }; [...it].join()"));
		Assert.Equal("a,b", Eval("const o = { *[Symbol.iterator]() { yield 'a'; yield 'b' } }; [...o].join()"));
		Assert.Equal("0:x,1:y", Eval("const r = []; for (const [i, v] of ['x', 'y'].entries()) r.push(i + ':' + v); r.join()"));
		Assert.Equal("3", Eval("const [a, b, c] = new Set([1, 2, 3]); c"));
		Assert.Equal("1,2", Eval("function* g() { yield 1; yield 2; yield 3 } const [x, y] = g(); [x, y].join()"));
	}

	[Fact]
	public void RunsPromisesAndAsyncFunctions()
	{
		List<string> log = Log(@"
			console.log('sync 1');
			Promise.resolve(1).then(v => console.log('then ' + v));
			queueMicrotask(() => console.log('micro'));
			new Promise((res, rej) => rej(new Error('bad'))).catch(e => console.log('caught ' + e.message)).finally(() => console.log('finally'));
			console.log('sync 2');");
		Assert.Equal(new[] { "sync 1", "sync 2", "then 1", "micro", "caught bad", "finally" }, log);
		Assert.Equal("3", Eval("let r; (async () => { const a = await 1; const b = await Promise.resolve(2); r = a + b })(); r"));
		Assert.Equal("fallback", Eval("async function f() { try { await Promise.reject(new Error('x')) } catch (e) { return 'fallback' } } await f()"));
		Assert.Equal("1,2,3", Eval("(await Promise.all([1, Promise.resolve(2), new Promise(res => res(3))])).join()"));
		Assert.Equal("fulfilled,rejected", Eval("(await Promise.allSettled([Promise.resolve(1), Promise.reject(2)])).map(x => x.status).join()"));
		Assert.Equal("first", Eval("await Promise.race([new Promise(() => {}), Promise.resolve('first')])"));
		Assert.Equal("2", Eval("await Promise.any([Promise.reject(1), Promise.resolve(2)])"));
		Assert.Equal("true", Eval("async function f() { return 1 } f() instanceof Promise"));
		Assert.Equal("chained", Eval("let r; Promise.resolve().then(() => 'chai').then(v => v + 'ned').then(v => r = v); await null; r"));
		Assert.Equal("thenable", Eval("await Promise.resolve({ then(res) { res('thenable') } })"));
		Assert.Equal("error", Eval("let r; Promise.resolve().then(() => { throw new Error('error') }).catch(e => r = e.message); await null; r"));
		Assert.Equal("x", Eval("try { await Promise.reject(new Error('x')) } catch (e) { e.message }"));
		List<string> unhandled = Log("Promise.reject(new Error('nobody'))");
		Assert.Single(unhandled);
		Assert.StartsWith("Uncaught (in promise) Error: nobody", unhandled[0]);
		Assert.Equal("done", Eval("class P extends Promise {} await P.resolve('done')"));
	}

	[Fact]
	public void RunsTimers()
	{
		List<string> log = new List<string>();
		Interpreter interpreter = New(log);
		double now = 0;
		interpreter.Clock = () => now;
		interpreter.Evaluate(@"
			setTimeout(() => console.log('b'), 20);
			setTimeout((x) => console.log('a' + x), 10, 1);
			const id = setTimeout(() => console.log('never'), 15);
			clearTimeout(id);
			let ticks = 0;
			const iv = setInterval(() => { ticks++; console.log('tick ' + ticks); if (ticks === 2) clearInterval(iv) }, 5);");
		Assert.True(interpreter.HasTimers);
		Assert.Equal(5, interpreter.NextTimerDue);
		now = 12;
		interpreter.RunTimers();
		Assert.Equal(new[] { "tick 1", "a1", "tick 2" }, log);
		now = 100;
		interpreter.RunTimers();
		Assert.Equal(new[] { "tick 1", "a1", "tick 2", "b" }, log);
		Assert.False(interpreter.HasTimers);
		Interpreter waiting = New(log);
		log.Clear();
		Assert.Equal("slept", waiting.Evaluate("let r; (async () => { await new Promise(res => setTimeout(res, 5)); r = 'slept' })(); r").ToJsString());
	}

	[Fact]
	public void ParsesRealWorldSyntax()
	{
		Assert.Equal("ok", Eval(@"
			// comments and ASI
			var a = 1
			var b = 2 /* inline */
			;[a, b].forEach(function (x) {})
			const re = /ab+c/i, s = 'x'.replace(/x/g, '$&')
			label: { break label }
			const big = 1_000_000, hex = 0xFF, oct = 0o17, bin = 0b11, exp = 1e3
			const obj = { 'quoted': 1, 2: 'num', [`comp${a}`]: 3, method() {}, get g() { return 1 }, set g(v) {}, async am() {}, *gen() {} }
			const fn = async (x) => await x
			const nested = a ? b ? 1 : 2 : 3
			let x = void 0, y = typeof x, z = delete obj.quoted
			if (a) b++; else b--
			for (var i = 0, j = 10; i < j; i++, j--) {}
			do b-- ; while (b > 0)
			x = y = 1
			new Date().getTime()
			new (function F() {})
			const {length} = 'str'
			const tpl = `line1
line2 ${`nested ${1 + 1}`}`
			class K { static #p = 1; static get p() { return K.#p } ['computed' + 1]() {} static { K.ran = true } }
			'ok'"));
		Assert.Equal("true", Eval("let get = 1, set = 2, of = 3, async = 4; get + set + of + async === 10"));
		Assert.Equal("2", Eval("const o = { if: 1, class: 2, new: 3 }; o.class"));
		Assert.Equal("5", Eval("var x = 5; { let x = 1; } x"));
		Assert.Equal("undefined", Eval("function f() { return\n1 } String(f())"));
		Assert.Equal("yes", Eval("'use strict'; const o = { 'a b': 'yes' }; o['a b']"));
		Assert.Equal("SyntaxError", Eval("try { eval('let let = 1 +') } catch (e) { e.name }"));
		Assert.Equal("4", Eval("new Function('a', 'b', 'return a + b')(1, 3)"));
		Assert.Equal("1,2", Eval("const a = [1, 2]; const [x = 5, y = 6] = a; [x, y].join()"));
		Assert.Equal("ok", Eval("let a = 1; let b; b = a > 0 ? 'ok' : 'no'; b"));
		Assert.Equal("true", Eval("const re = /[/\\]]+/; re.test('/]') && 1 / 2 / 1 === 0.5"));
		Assert.Equal("3", Eval("var o = {a: {b: {c: 3}}}; o?.a?.['b']?.c"));
		Assert.Equal("fn", Eval("const o = { f() { return 'fn' } }; o?.f?.()"));
		Assert.Equal("undefined", Eval("const o = {}; String(o.f?.())"));
		Assert.Equal("1", Eval("(function() { return this })() === globalThis ? 1 : 0"));
		Assert.Equal("true", Eval("(() => this)() === globalThis && (function() { 'use strict'; return this })() === undefined"));
		Assert.Equal("w", Eval("var g = 'w'; window.g"));
		Assert.Equal("true", Eval("x = 5; globalThis.x === 5 && delete globalThis.x && typeof x === 'undefined'"));
	}

	[Fact]
	public void ProvidesTypedArraysAndProxies()
	{
		Assert.Equal("44|3|1.5|2|2,4,88|513|2|true|[object Float32Array]|true|[-1,-1]|255,0,2", Eval(@"
			const u = new Uint8Array([1, 2, 300]); const f = new Float32Array(2); f[0] = 1.5; const dv = new DataView(u.buffer);
			[u[2], u.length, f[0], dv.getUint8(1), Array.from(u.map(x => x * 2)).join(), new Uint16Array(u.buffer, 0, 1)[0], u.subarray(1).length, ArrayBuffer.isView(u), Object.prototype.toString.call(f), u instanceof Uint8Array, JSON.stringify(Array.from(new Int8Array([-1, 255]))), Array.from(new Uint8ClampedArray([300, -5, 1.5])).join()].join('|')"));
		Assert.Equal("16|4|-2|1.5|3,1,2|3|255", Eval(@"
			const buffer = new ArrayBuffer(16); const view = new DataView(buffer); view.setInt16(0, -2); view.setFloat64(8, 1.5, true);
			const bytes = new Uint8Array(buffer); const copy = new Uint8Array(buffer.slice(8, 12)); const i32 = Int32Array.of(3, 1, 2);
			[buffer.byteLength, copy.length, view.getInt16(0), view.getFloat64(8, true), [...i32].join(), i32.reduce((a, b) => Math.max(a, b)), bytes.fill(255)[15]].join('|')"));
		Assert.Equal("1|dflt|10|true|false|true|a,zzz|get:a;get:missing;get:b;del:a", Eval(@"
			const log = []; const p = new Proxy({a: 1}, {
				get(t, k, r) { log.push('get:' + String(k)); return k in t ? t[k] : 'dflt' },
				set(t, k, v) { t[k] = v * 2; return true },
				has(t, k) { return k === 'ghost' || k in t },
				deleteProperty(t, k) { log.push('del:' + k); delete t[k]; return true },
				ownKeys(t) { return ['a', 'zzz'] },
				getOwnPropertyDescriptor(t, k) { return { value: t[k], enumerable: true, configurable: true } } });
			p.b = 5;
			[p.a, p.missing, p.b, 'ghost' in p, 'nope' in p, delete p.a, Object.keys(p).join(), log.join(';')].join('|')"));
		Assert.Equal("20|2|TypeError|true", Eval(@"
			const fp = new Proxy(function (x) { return x + 1 }, { apply(t, th, args) { return t(...args) * 10 } });
			const P = new Proxy(class A { constructor(v) { this.v = v } }, { construct(t, args) { return new t(args[0] + 1) } });
			const { proxy, revoke } = Proxy.revocable({}, {}); revoke(); let err = ''; try { proxy.x } catch (e) { err = e.name }
			[fp(1), new P(1).v, err, typeof fp === 'function'].join('|')"));
	}

	[Fact]
	public void LinksAndRunsModules()
	{
		Dictionary<string, string> sources = new Dictionary<string, string>
		{
			["https://m.test/lib/counter.js"] = "export let count = 0; export function bump() { count++ } export default class Thing { static name2 = 'thing' } export const pi = 3; export { pi as PI }",
			["https://m.test/lib/index.js"] = "export * from './counter.js'; export { default as Thing } from './counter.js'; export const extra = import.meta.url",
			["https://m.test/a.js"] = "import { b } from './b.js'; export const a = 'a' + (typeof b); export function fromA() { return b() }",
			["https://m.test/b.js"] = "import { a } from './a.js'; export function b() { return 'b' + a }",
			["https://m.test/bad.js"] = "import { missing } from './b.js';",
		};
		Interpreter interpreter = New();
		interpreter.FetchModule = url => sources.TryGetValue(url, out string source) ? source : null;
		interpreter.EvaluateModule(@"
			import Thing, { count, bump, pi } from './lib/counter.js';
			import * as lib from './lib/index.js';
			import { fromA, a } from './a.js';
			bump(); bump();
			const dyn = await import('./lib/index.js');
			let failed = '';
			try { await import('./bad.js') } catch (e) { failed = e.name }
			globalThis.result = [count, lib.count, lib.PI, pi, Thing === lib.Thing, lib.extra, dyn === lib, Object.prototype.toString.call(lib), fromA(), a, failed, typeof lib.default, this];
		", "https://m.test/main.js");
		Assert.Equal("2|2|3|3|true|https://m.test/lib/index.js|true|[object Module]|bafunction|afunction|SyntaxError|undefined|", interpreter.Evaluate("result.join('|')").ToJsString());
		Assert.Equal("true", interpreter.Evaluate("(function() { return this === undefined })() === false && typeof import.meta.url === 'string'").ToJsString());
	}

	[Fact]
	public void TracksLinesInStackTraces()
	{
		List<string> log = Log("function inner() {\n  throw new Error('deep');\n}\nfunction outer() {\n  inner();\n}\nouter();");
		Assert.Single(log);
		Assert.Contains("Error: deep", log[0]);
		Assert.Contains("at inner (script:2)", log[0]);
		Assert.Contains("at outer (script:5)", log[0]);
		Assert.Contains("at <script> (script:7)", log[0]);
	}

	[Fact]
	public void ExposesHostObjects()
	{
		Interpreter interpreter = New();
		JsObject host = interpreter.NewObject();
		int calls = 0;
		interpreter.DefineMethod(host, "ping", 1, (i, t, a) =>
		{
			calls++;
			return "pong:" + Interpreter.Arg(a, 0).ToJsString();
		});
		interpreter.DefineGetter(host, "count", (i, t, a) => calls);
		interpreter.Global.Put("host", host);
		Assert.Equal("pong:1|pong:2|2", interpreter.Evaluate("[host.ping(1), host.ping(2), host.count].join('|')").ToJsString());
		JsValue callback = interpreter.Evaluate("(function(a, b) { return this.k + a + b })");
		JsObject thisObject = interpreter.NewObject();
		thisObject.Put("k", "k");
		Assert.Equal("kab", interpreter.Call(callback, thisObject, new JsValue[] { "a", "b" }).ToJsString());
		Assert.Equal("[object Object]", interpreter.Evaluate("String(host)").ToJsString());
	}
}
