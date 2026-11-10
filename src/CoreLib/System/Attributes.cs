namespace System
{
	public abstract class Attribute
	{
	}

	[Flags]
	public enum AttributeTargets
	{
		Assembly = 0x0001,
		Module = 0x0002,
		Class = 0x0004,
		Struct = 0x0008,
		Enum = 0x0010,
		Constructor = 0x0020,
		Method = 0x0040,
		Property = 0x0080,
		Field = 0x0100,
		Event = 0x0200,
		Interface = 0x0400,
		Parameter = 0x0800,
		Delegate = 0x1000,
		ReturnValue = 0x2000,
		GenericParameter = 0x4000,
		All = 0x7FFF,
	}

	[AttributeUsage(AttributeTargets.Class, Inherited = true)]
	public sealed class AttributeUsageAttribute : Attribute
	{
		public AttributeUsageAttribute(AttributeTargets validOn)
		{
			ValidOn = validOn;
		}

		public AttributeTargets ValidOn { get; }
		public bool AllowMultiple { get; set; }
		public bool Inherited { get; set; }
	}

	[AttributeUsage(AttributeTargets.Enum)]
	public sealed class FlagsAttribute : Attribute
	{
	}

	[AttributeUsage(AttributeTargets.Parameter)]
	public sealed class ParamArrayAttribute : Attribute
	{
	}

	[AttributeUsage(AttributeTargets.All, Inherited = false)]
	public sealed class ObsoleteAttribute : Attribute
	{
		public ObsoleteAttribute()
		{
		}

		public ObsoleteAttribute(string message)
		{
		}
	}

	[AttributeUsage(AttributeTargets.Field, Inherited = false)]
	public sealed class ThreadStaticAttribute : Attribute
	{
	}
}

namespace System.Diagnostics
{
	[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
	public sealed class ConditionalAttribute : Attribute
	{
		public ConditionalAttribute(string conditionString)
		{
		}
	}
}

namespace System.Reflection
{
	// The compiler marks types that declare an indexer with this
	[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface)]
	public sealed class DefaultMemberAttribute : Attribute
	{
		public DefaultMemberAttribute(string memberName)
		{
			MemberName = memberName;
		}

		public string MemberName { get; }
	}
}
