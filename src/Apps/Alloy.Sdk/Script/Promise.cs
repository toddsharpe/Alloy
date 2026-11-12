using System;
using System.Collections.Generic;
using System.Threading;

namespace Alloy.Sdk.Script
{
	public enum PromiseState
	{
		Pending,
		Fulfilled,
		Rejected,
	}

	// A promise: its state and value, and the reactions waiting for them
	public sealed class JsPromise : JsObject
	{
		public PromiseState State;
		public JsValue Value;
		public bool Handled;
		internal List<Action> Reactions;

		public JsPromise(JsObject prototype) : base(prototype)
		{
			Class = "Promise";
		}
	}

	public delegate bool PumpHandler();

	public delegate double ClockHandler();

	// Promises, the job queue their reactions run on, timers, and the synchronous await that drives both
	public sealed partial class Interpreter
	{
		public sealed class Timer
		{
			public int Id;
			public double Due;
			public JsFunction Callback;
			public JsValue[] Arguments;
			public double Interval = -1;
		}

		private readonly Queue<Action> _jobs = new Queue<Action>();
		private readonly List<JsPromise> _unhandled = new List<JsPromise>();
		private readonly List<Timer> _timers = new List<Timer>();
		private int _nextTimerId = 1;
		public PumpHandler HostPump;
		public ClockHandler Clock = () => Environment.TickCount64;

		private void SetupPromise()
		{
			PromiseConstructor = NewConstructor("Promise", 1, PromisePrototype, null, (i, a, target) =>
			{
				JsFunction executor = RequireCallable(Arg(a, 0), "Promise resolver " + Arg(a, 0).ToJsString());
				JsPromise promise = new JsPromise(PrototypeFromConstructor(target, PromisePrototype));
				try
				{
					Call(executor, JsValue.Undefined, new JsValue[] { ResolveFunction(promise), RejectFunction(promise) });
				}
				catch (JsThrow t)
				{
					RejectPromise(promise, t.Value);
				}
				return promise;
			});
			DefineMethod(PromiseConstructor, "resolve", 1, (i, t, a) => PromiseResolve(Arg(a, 0)));
			DefineMethod(PromiseConstructor, "reject", 1, (i, t, a) =>
			{
				JsPromise promise = NewPromise();
				RejectPromise(promise, Arg(a, 0));
				return promise;
			});
			DefineMethod(PromiseConstructor, "withResolvers", 0, (i, t, a) =>
			{
				JsPromise promise = NewPromise();
				JsObject result = NewObject();
				result.Put("promise", promise);
				result.Put("resolve", ResolveFunction(promise));
				result.Put("reject", RejectFunction(promise));
				return result;
			});
			DefineMethod(PromiseConstructor, "all", 1, (i, t, a) => Combine(Arg(a, 0), CombineKind.All));
			DefineMethod(PromiseConstructor, "allSettled", 1, (i, t, a) => Combine(Arg(a, 0), CombineKind.AllSettled));
			DefineMethod(PromiseConstructor, "any", 1, (i, t, a) => Combine(Arg(a, 0), CombineKind.Any));
			DefineMethod(PromiseConstructor, "race", 1, (i, t, a) =>
			{
				JsPromise result = NewPromise();
				foreach (JsValue item in Iterate(Arg(a, 0)))
					Then(PromiseResolve(item), v => ResolvePromise(result, v), e => RejectPromise(result, e));
				return result;
			});
			DefineMethod(PromisePrototype, "then", 2, (i, t, a) => PromiseThen(ThisPromise(t), Arg(a, 0), Arg(a, 1)));
			DefineMethod(PromisePrototype, "catch", 1, (i, t, a) => PromiseThen(ThisPromise(t), JsValue.Undefined, Arg(a, 0)));
			DefineMethod(PromisePrototype, "finally", 1, (i, t, a) =>
			{
				JsPromise promise = ThisPromise(t);
				if (!Arg(a, 0).IsCallable)
					return PromiseThen(promise, Arg(a, 0), Arg(a, 0));
				JsFunction callback = a[0].Function;
				JsPromise result = NewPromise();
				Then(promise, value =>
				{
					try
					{
						JsValue inner = Call(callback, JsValue.Undefined, Array.Empty<JsValue>());
						Then(PromiseResolve(inner), v => ResolvePromise(result, value), e => RejectPromise(result, e));
					}
					catch (JsThrow x)
					{
						RejectPromise(result, x.Value);
					}
				}, reason =>
				{
					try
					{
						JsValue inner = Call(callback, JsValue.Undefined, Array.Empty<JsValue>());
						Then(PromiseResolve(inner), v => RejectPromise(result, reason), e => RejectPromise(result, e));
					}
					catch (JsThrow x)
					{
						RejectPromise(result, x.Value);
					}
				});
				return result;
			});
			PromisePrototype.Put(SymbolToStringTag, "Promise", false);
		}

		private enum CombineKind
		{
			All,
			AllSettled,
			Any,
		}

		private JsValue Combine(JsValue iterable, CombineKind kind)
		{
			JsPromise result = NewPromise();
			List<JsValue> values = new List<JsValue>();
			int remaining = 1;
			int index = 0;
			foreach (JsValue item in Iterate(iterable))
			{
				int slot = index++;
				values.Add(JsValue.Undefined);
				remaining++;
				Then(PromiseResolve(item), value =>
				{
					if (kind == CombineKind.Any)
					{
						ResolvePromise(result, value);
						return;
					}
					values[slot] = kind == CombineKind.AllSettled ? Settled("fulfilled", "value", value) : value;
					if (--remaining == 0)
						ResolvePromise(result, NewArray(values));
				}, reason =>
				{
					if (kind == CombineKind.All)
					{
						RejectPromise(result, reason);
						return;
					}
					values[slot] = kind == CombineKind.AllSettled ? Settled("rejected", "reason", reason) : reason;
					if (--remaining == 0)
					{
						if (kind == CombineKind.Any)
						{
							JsObject error = NewError("AggregateError", "All promises were rejected");
							error.Put("errors", NewArray(values), false);
							RejectPromise(result, error);
						}
						else
							ResolvePromise(result, NewArray(values));
					}
				});
			}
			if (--remaining == 0)
			{
				if (kind == CombineKind.Any)
				{
					JsObject error = NewError("AggregateError", "All promises were rejected");
					error.Put("errors", NewArray(values), false);
					RejectPromise(result, error);
				}
				else
					ResolvePromise(result, NewArray(values));
			}
			return result;
		}

		private JsObject Settled(string status, string key, JsValue value)
		{
			JsObject result = NewObject();
			result.Put("status", status);
			result.Put(key, value);
			return result;
		}

		private JsPromise ThisPromise(JsValue thisValue)
		{
			if (thisValue.IsObject && thisValue.Object is JsPromise promise)
				return promise;
			throw TypeError("Method Promise.prototype.then called on incompatible receiver " + thisValue.ToJsString());
		}

		public JsPromise NewPromise() => new JsPromise(PromisePrototype);

		private JsFunction ResolveFunction(JsPromise promise)
		{
			bool done = false;
			return NewNative("", 1, (i, t, a) =>
			{
				if (!done)
				{
					done = true;
					ResolvePromise(promise, Arg(a, 0));
				}
				return JsValue.Undefined;
			});
		}

		private JsFunction RejectFunction(JsPromise promise)
		{
			bool done = false;
			return NewNative("", 1, (i, t, a) =>
			{
				if (!done)
				{
					done = true;
					RejectPromise(promise, Arg(a, 0));
				}
				return JsValue.Undefined;
			});
		}

		// Resolves with a value, following a thenable through its then method on a later job
		public void ResolvePromise(JsPromise promise, JsValue value)
		{
			if (promise.State != PromiseState.Pending)
				return;
			if (value.IsObject && ReferenceEquals(value.Object, promise))
			{
				RejectPromise(promise, NewError("TypeError", "Chaining cycle detected for promise"));
				return;
			}
			if (value.IsObject && value.Object is JsPromise inner)
			{
				Then(inner, v => ResolvePromise(promise, v), e => RejectPromise(promise, e));
				return;
			}
			if (value.IsObject)
			{
				JsValue then;
				try
				{
					then = GetMember(value, "then");
				}
				catch (JsThrow t)
				{
					RejectPromise(promise, t.Value);
					return;
				}
				if (then.IsCallable)
				{
					EnqueueJob(() =>
					{
						try
						{
							Call(then.Function, value, new JsValue[] { ResolveFunction(promise), RejectFunction(promise) });
						}
						catch (JsThrow t)
						{
							RejectPromise(promise, t.Value);
						}
					});
					return;
				}
			}
			Settle(promise, PromiseState.Fulfilled, value);
		}

		public void RejectPromise(JsPromise promise, JsValue reason)
		{
			if (promise.State != PromiseState.Pending)
				return;
			Settle(promise, PromiseState.Rejected, reason);
			if (!promise.Handled)
				_unhandled.Add(promise);
		}

		private void Settle(JsPromise promise, PromiseState state, JsValue value)
		{
			promise.State = state;
			promise.Value = value;
			List<Action> reactions = promise.Reactions;
			promise.Reactions = null;
			if (reactions == null)
				return;
			foreach (Action reaction in reactions)
				EnqueueJob(reaction);
		}

		// Registers host-side reactions, run as jobs once the promise settles
		public void Then(JsPromise promise, Action<JsValue> onFulfilled, Action<JsValue> onRejected)
		{
			promise.Handled = true;
			_unhandled.Remove(promise);
			Action reaction = () =>
			{
				if (promise.State == PromiseState.Fulfilled)
					onFulfilled(promise.Value);
				else
					onRejected(promise.Value);
			};
			if (promise.State == PromiseState.Pending)
				(promise.Reactions ??= new List<Action>()).Add(reaction);
			else
				EnqueueJob(reaction);
		}

		private JsValue PromiseThen(JsPromise promise, JsValue onFulfilled, JsValue onRejected)
		{
			JsPromise result = NewPromise();
			Then(promise, value =>
			{
				if (!onFulfilled.IsCallable)
				{
					ResolvePromise(result, value);
					return;
				}
				try
				{
					ResolvePromise(result, Call(onFulfilled.Function, JsValue.Undefined, new[] { value }));
				}
				catch (JsThrow t)
				{
					RejectPromise(result, t.Value);
				}
			}, reason =>
			{
				if (!onRejected.IsCallable)
				{
					RejectPromise(result, reason);
					return;
				}
				try
				{
					ResolvePromise(result, Call(onRejected.Function, JsValue.Undefined, new[] { reason }));
				}
				catch (JsThrow t)
				{
					RejectPromise(result, t.Value);
				}
			});
			return result;
		}

		public JsPromise PromiseResolve(JsValue value)
		{
			if (value.IsObject && value.Object is JsPromise promise)
				return promise;
			JsPromise result = NewPromise();
			ResolvePromise(result, value);
			return result;
		}

		// Jobs

		public void EnqueueJob(Action job) => _jobs.Enqueue(job);

		// Runs queued jobs until none remain, then reports rejections nobody handled; true when anything ran
		public bool RunJobs()
		{
			bool any = false;
			Interpreter prior = Current;
			Current = this;
			try
			{
				while (_jobs.Count > 0)
				{
					any = true;
					Action job = _jobs.Dequeue();
					try
					{
						job();
					}
					catch (JsThrow t)
					{
						ReportError(t.Value);
					}
				}
				if (_unhandled.Count > 0)
				{
					List<JsPromise> unhandled = new List<JsPromise>(_unhandled);
					_unhandled.Clear();
					foreach (JsPromise promise in unhandled)
					{
						if (!promise.Handled)
							OnError?.Invoke("Uncaught (in promise) " + DescribeError(promise.Value));
					}
				}
			}
			finally
			{
				Current = prior;
			}
			return any;
		}

		// Async functions run synchronously; an await drains the job queue, and then the host, until its promise settles
		private JsValue CallAsync(JsFunction function, JsValue thisValue, JsValue[] arguments)
		{
			JsPromise promise = NewPromise();
			try
			{
				JsValue result = RunBody(function, NewFunctionScope(function, thisValue, JsValue.Undefined), arguments);
				ResolvePromise(promise, result);
			}
			catch (JsThrow t)
			{
				RejectPromise(promise, t.Value);
			}
			return promise;
		}

		public JsValue Await(JsValue value)
		{
			JsPromise promise = PromiseResolve(value);
			promise.Handled = true;
			_unhandled.Remove(promise);
			RunJobs();
			while (promise.State == PromiseState.Pending)
			{
				if (RunJobs())
					continue;
				// A promise nothing will ever settle, kept company by an interval, would otherwise hold the thread forever
				if (Clock() > Deadline)
					throw RangeError("Script ran too long");
				if (!(HostPump ?? DefaultPump)())
					break;
			}
			if (promise.State == PromiseState.Rejected)
				throw new JsThrow(promise.Value);
			return promise.State == PromiseState.Fulfilled ? promise.Value : JsValue.Undefined;
		}

		private bool DefaultPump()
		{
			if (_timers.Count == 0)
				return false;
			double wait = Math.Min(NextTimerDue, Deadline) - Clock();
			if (wait > 0)
				Thread.Sleep((int)Math.Min(wait, 250));
			RunTimers();
			return true;
		}

		// Timers

		private void SetupTimers()
		{
			DefineMethod(Global, "setTimeout", 2, (i, t, a) => AddTimer(Arg(a, 0), Arg(a, 1), a, false));
			DefineMethod(Global, "setInterval", 2, (i, t, a) => AddTimer(Arg(a, 0), Arg(a, 1), a, true));
			DefineMethod(Global, "setImmediate", 1, (i, t, a) => AddTimer(Arg(a, 0), 0, a, false));
			DefineMethod(Global, "clearTimeout", 1, (i, t, a) =>
			{
				ClearTimer(Arg(a, 0));
				return JsValue.Undefined;
			});
			Global.Put("clearInterval", Global.Get("clearTimeout", this), false);
			Global.Put("clearImmediate", Global.Get("clearTimeout", this), false);
			DefineMethod(Global, "queueMicrotask", 1, (i, t, a) =>
			{
				JsFunction callback = RequireCallable(Arg(a, 0), "queueMicrotask callback");
				EnqueueJob(() => Call(callback, JsValue.Undefined, Array.Empty<JsValue>()));
				return JsValue.Undefined;
			});
			DefineMethod(Global, "requestAnimationFrame", 1, (i, t, a) => AddTimer(Arg(a, 0), 16, new[] { Arg(a, 0), 16, Clock() }, false));
			Global.Put("cancelAnimationFrame", Global.Get("clearTimeout", this), false);
		}

		private JsValue AddTimer(JsValue callback, JsValue delay, JsValue[] arguments, bool repeat)
		{
			if (!callback.IsCallable)
				return 0;
			double ms = delay.ToNumber();
			if (double.IsNaN(ms) || ms < 0)
				ms = 0;
			Timer timer = new Timer
			{
				Id = _nextTimerId++,
				Due = Clock() + ms,
				Callback = callback.Function,
				Arguments = arguments.Length > 2 ? arguments.AsSpan(2).ToArray() : Array.Empty<JsValue>(),
				Interval = repeat ? Math.Max(1, ms) : -1,
			};
			_timers.Add(timer);
			return timer.Id;
		}

		private void ClearTimer(JsValue id)
		{
			int n = id.ToInt32();
			_timers.RemoveAll(t => t.Id == n);
		}

		public bool HasTimers => _timers.Count > 0;

		public double NextTimerDue
		{
			get
			{
				double due = double.PositiveInfinity;
				foreach (Timer timer in _timers)
					due = Math.Min(due, timer.Due);
				return due;
			}
		}

		// Runs every timer that is due, earliest first, and the jobs each one queues; true when any ran
		public bool RunTimers()
		{
			bool any = false;
			Interpreter prior = Current;
			Current = this;
			try
			{
				for (int round = 0; round < 100; round++)
				{
					double now = Clock();
					List<Timer> due = new List<Timer>();
					foreach (Timer timer in _timers)
					{
						if (timer.Due <= now)
							due.Add(timer);
					}
					if (due.Count == 0)
						break;
					any = true;
					due.Sort((a, b) => a.Due != b.Due ? a.Due.CompareTo(b.Due) : a.Id.CompareTo(b.Id));
					foreach (Timer timer in due)
					{
						if (!_timers.Contains(timer))
							continue;
						if (timer.Interval >= 0)
							timer.Due = timer.Due + timer.Interval < now - 1000 ? now + timer.Interval : timer.Due + timer.Interval;
						else
							_timers.Remove(timer);
						try
						{
							Call(timer.Callback, Global, timer.Arguments);
						}
						catch (JsThrow t)
						{
							ReportError(t.Value);
						}
						RunJobs();
					}
				}
			}
			finally
			{
				Current = prior;
			}
			return any;
		}
	}
}
