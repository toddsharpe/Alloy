using System;
using System.Text;
using Alloy.Sdk;
using Alloy.Sdk.Script;
using Alloy.Sdk.Web;

namespace Alloy.Apps.JsApp
{
	// JavaScript from the terminal: js <file or url> runs a script, js -e <source> runs a snippet, js alone runs the engine's self-test
	internal static class Program
	{
		private static int Main(string[] args)
		{
			string source;
			if (args.Length == 0)
				source = SelfTest;
			else if (args[0] == "-e")
				source = string.Join(" ", args, 1, args.Length - 1);
			else if (args[0].Contains("://") || args[0].StartsWith("www.", StringComparison.OrdinalIgnoreCase))
			{
				HttpResponse response = Http.Get(args[0].Contains("://") ? args[0] : "https://" + args[0]);
				if (!response.Ok)
				{
					Console.WriteLine($"js: {response.Error ?? response.Status.ToString()}");
					return 1;
				}
				source = response.BodyText();
			}
			else
			{
				byte[] bytes = File.ReadAllBytes(args[0]);
				if (bytes == null)
				{
					Console.WriteLine($"js: can't read {args[0]}");
					return 1;
				}
				source = Encoding.UTF8.GetString(bytes);
			}

			Interpreter interpreter = new Interpreter();
			interpreter.OnConsole = (level, text) => Console.WriteLine(level == "log" || level == "info" || level == "debug" ? text : level + ": " + text);
			interpreter.OnError = text => Console.WriteLine(text);
			ulong start = Kernel.TickCount;
			bool ok = interpreter.TryEvaluate(source);
			// Timers keep the program alive until the last one has fired
			while (interpreter.HasTimers)
			{
				double wait = interpreter.NextTimerDue - interpreter.Clock();
				if (wait > 0)
					Kernel.Sleep((ulong)Math.Min(wait, 100));
				interpreter.RunTimers();
			}
			Console.WriteLine($"js: {(ok ? "done" : "failed")} in {Kernel.TickCount - start} ms");
			return ok ? 0 : 1;
		}

		// A sweep across the engine: each line prints what a browser would, so a glance at the terminal shows what differs
		private const string SelfTest = @"
			const results = [];
			function check(name, actual, expected) { results.push((String(actual) === String(expected) ? 'ok   ' : 'FAIL ') + name + ': ' + actual); }
			check('arithmetic', 0.1 + 0.2, '0.30000000000000004');
			check('template', `${1 + 1} ${'a'.toUpperCase()}`, '2 A');
			check('closure', (() => { let n = 0; const inc = () => ++n; inc(); return inc(); })(), 2);
			check('destructuring', (({a, b: [c]}) => a + c)({a: 1, b: [2]}), 3);
			class Point { constructor(x, y) { this.x = x; this.y = y } get length() { return Math.hypot(this.x, this.y) } static origin() { return new Point(0, 0) } }
			class Point3 extends Point { constructor(x, y, z) { super(x, y); this.z = z } toString() { return `(${this.x},${this.y},${this.z})` } }
			check('classes', String(new Point3(3, 4, 5)) + ' ' + new Point(3, 4).length + ' ' + (Point.origin() instanceof Point), '(3,4,5) 5 true');
			check('regexp', 'John Smith'.replace(/(\w+)\s(\w+)/, '$2, $1') + ' ' + /^\d{3}-\d{4}$/.test('555-1234'), 'Smith, John true');
			check('array', [5, 3, 9, 1].sort((a, b) => a - b).map(n => n * 2).filter(n => n > 2).join('|'), '6|10|18');
			check('json', JSON.stringify(JSON.parse('{""a"":[1,{""b"":null}],""c"":""é""}')), '{""a"":[1,{""b"":null}],""c"":""é""}');
			check('date', new Date(Date.UTC(2024, 0, 15, 10, 30)).toISOString(), '2024-01-15T10:30:00.000Z');
			check('map/set', [...new Map([['a', 1]]).keys()].join() + new Set([1, 1, 2]).size, 'a2');
			function* fib() { let [a, b] = [0, 1]; while (true) { yield a; [a, b] = [b, a + b] } }
			const first = []; for (const n of fib()) { if (first.length === 8) break; first.push(n) }
			check('generator', first.join(), '0,1,1,2,3,5,8,13');
			let order = 'a';
			Promise.resolve().then(() => order += 'c');
			order += 'b';
			check('try/finally', (() => { try { throw new TypeError('x') } catch (e) { return e.name } finally { order += 'd' } })(), 'TypeError');
			(async () => {
				const v = await new Promise(res => setTimeout(() => res('later'), 10));
				check('await timer', v + ' ' + order, 'later abdc');
				check('symbol', typeof Symbol.iterator + ' ' + Object.prototype.toString.call([]), 'symbol [object Array]');
				check('stack', new Error('m').stack.split('\n')[0], 'Error: m');
				results.forEach(r => console.log(r));
				const failed = results.filter(r => r.startsWith('FAIL')).length;
				console.log(failed === 0 ? `js: all ${results.length} checks pass` : `js: ${failed} of ${results.length} checks FAIL`);
			})();";
	}
}
