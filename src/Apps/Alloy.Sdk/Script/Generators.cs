using System;
using System.Threading;

namespace Alloy.Sdk.Script
{
	// Thrown inside a generator body when the consumer calls return(), so finally blocks run on the way out
	internal sealed class GeneratorSignal : Exception
	{
		public readonly JsValue Value;

		public GeneratorSignal(JsValue value)
		{
			Value = value;
		}
	}

	internal enum GeneratorState
	{
		SuspendedStart,
		SuspendedYield,
		Running,
		Completed,
	}

	public enum ResumeMode
	{
		Next,
		Throw,
		Return,
	}

	// A generator runs its body on its own thread, handing control back and forth with the caller at each yield
	public sealed class JsGenerator : JsObject
	{
		private readonly Interpreter _interpreter;
		private readonly JsFunction _function;
		private readonly JsValue _thisValue;
		private readonly JsValue[] _arguments;
		private readonly AutoResetEvent _resume = new AutoResetEvent(false);
		private readonly AutoResetEvent _yielded = new AutoResetEvent(false);
		private Thread _thread;
		private GeneratorState _state = GeneratorState.SuspendedStart;
		private ResumeMode _mode;
		private JsValue _sent;
		private JsValue _produced;
		private bool _done;
		private Exception _failure;

		public JsGenerator(JsObject prototype, Interpreter interpreter, JsFunction function, JsValue thisValue, JsValue[] arguments) : base(prototype)
		{
			Class = "Generator";
			_interpreter = interpreter;
			_function = function;
			_thisValue = thisValue;
			_arguments = arguments;
		}

		[ThreadStatic]
		internal static JsGenerator Running;

		public JsValue Resume(ResumeMode mode, JsValue value)
		{
			if (_state == GeneratorState.Running)
				throw _interpreter.TypeError("Generator is already running");
			if (_state == GeneratorState.Completed)
			{
				if (mode == ResumeMode.Throw)
					throw new JsThrow(value);
				return JsArrayIterator.IteratorResult(_interpreter, mode == ResumeMode.Return ? value : JsValue.Undefined, true);
			}
			if (_state == GeneratorState.SuspendedStart && mode != ResumeMode.Next)
			{
				_state = GeneratorState.Completed;
				if (mode == ResumeMode.Throw)
					throw new JsThrow(value);
				return JsArrayIterator.IteratorResult(_interpreter, value, true);
			}
			_mode = mode;
			_sent = value;
			bool first = _state == GeneratorState.SuspendedStart;
			_state = GeneratorState.Running;
			JsGenerator outer = Running;
			if (first)
			{
				_thread = new Thread(Body, 4 * 1024 * 1024) { IsBackground = true, Name = "generator" };
				_thread.Start();
			}
			else
				_resume.Set();
			_yielded.WaitOne();
			Running = outer;
			if (_failure != null)
			{
				Exception failure = _failure;
				_failure = null;
				_state = GeneratorState.Completed;
				if (failure is GeneratorSignal signal)
					return JsArrayIterator.IteratorResult(_interpreter, signal.Value, true);
				throw failure;
			}
			if (_done)
				_state = GeneratorState.Completed;
			else
				_state = GeneratorState.SuspendedYield;
			return JsArrayIterator.IteratorResult(_interpreter, _produced, _done);
		}

		private void Body()
		{
			Running = this;
			Interpreter.Current = _interpreter;
			try
			{
				Scope scope = _interpreter.NewGeneratorScope(_function, _thisValue);
				_produced = _interpreter.RunBody(_function, scope, _arguments);
				_done = true;
			}
			catch (Exception e)
			{
				_failure = e;
				_done = true;
			}
			_yielded.Set();
		}

		// Called on the generator thread: hands the value to the caller and waits to be resumed
		internal JsValue Yield(JsValue value)
		{
			_produced = value;
			_done = false;
			_yielded.Set();
			_resume.WaitOne();
			Running = this;
			switch (_mode)
			{
				case ResumeMode.Throw:
					throw new JsThrow(_sent);
				case ResumeMode.Return:
					throw new GeneratorSignal(_sent);
				default:
					return _sent;
			}
		}
	}

	public sealed partial class Interpreter
	{
		private void SetupGenerators()
		{
			DefineMethod(GeneratorPrototype, "next", 1, (i, t, a) => ThisGenerator(t).Resume(ResumeMode.Next, Arg(a, 0)));
			DefineMethod(GeneratorPrototype, "return", 1, (i, t, a) => ThisGenerator(t).Resume(ResumeMode.Return, Arg(a, 0)));
			DefineMethod(GeneratorPrototype, "throw", 1, (i, t, a) => ThisGenerator(t).Resume(ResumeMode.Throw, Arg(a, 0)));
			GeneratorPrototype.Put(SymbolToStringTag, "Generator", false);
		}

		private JsGenerator ThisGenerator(JsValue thisValue)
		{
			if (thisValue.IsObject && thisValue.Object is JsGenerator generator)
				return generator;
			throw TypeError("next method called on incompatible receiver " + thisValue.ToJsString());
		}

		private JsValue CreateGenerator(JsFunction function, JsValue thisValue, JsValue[] arguments)
		{
			JsValue prototype = GetMember(function, "prototype");
			return new JsGenerator(prototype.IsObject ? prototype.Object : GeneratorPrototype, this, function, thisValue, arguments);
		}

		internal Scope NewGeneratorScope(JsFunction function, JsValue thisValue) => NewFunctionScope(function, thisValue, JsValue.Undefined);

		private JsValue EvaluateYield(YieldExpression yield, Scope scope)
		{
			JsGenerator generator = JsGenerator.Running;
			if (generator == null)
				throw SyntaxErrorThrow("yield outside a generator");
			JsValue argument = yield.Argument == null ? JsValue.Undefined : Evaluate(yield.Argument, scope);
			if (!yield.Delegate)
				return generator.Yield(argument);
			JsValue last = JsValue.Undefined;
			foreach (JsValue item in Iterate(argument))
				last = generator.Yield(item);
			return last;
		}
	}
}
