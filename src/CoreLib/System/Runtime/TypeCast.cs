using System.Runtime.CompilerServices;
using Internal.Runtime;

namespace System.Runtime
{
	// Casts and covariant array stores for generated code; variance and IDynamicInterfaceCastable fail loudly
	internal static class TypeCast
	{
		public static unsafe object IsInstanceOfClass(MethodTable* pTargetType, object obj)
		{
			if (obj == null)
				return null;
			for (MethodTable* type = obj.GetMethodTable(); type != null; type = type->BaseType)
			{
				if (type == pTargetType)
					return obj;
			}
			return null;
		}

		public static unsafe object CheckCastClass(MethodTable* pTargetType, object obj)
		{
			if (obj != null && IsInstanceOfClass(pTargetType, obj) == null)
				ThrowHelper.ThrowInvalidCast();
			return obj;
		}

		private static unsafe object CheckCastClassSpecial(MethodTable* pTargetType, object obj) => CheckCastClass(pTargetType, obj);

		public static unsafe object IsInstanceOfInterface(MethodTable* pTargetType, object obj)
		{
			if (obj == null)
				return null;
			return ImplementsInterface(obj.GetMethodTable(), pTargetType) ? obj : null;
		}

		public static unsafe object CheckCastInterface(MethodTable* pTargetType, object obj)
		{
			if (obj != null && !ImplementsInterface(obj.GetMethodTable(), pTargetType))
				ThrowHelper.ThrowInvalidCast();
			return obj;
		}

		public static unsafe object IsInstanceOfAny(MethodTable* pTargetType, object obj)
		{
			if (obj == null)
				return null;
			return AreTypesAssignable(obj.GetMethodTable(), pTargetType) ? obj : null;
		}

		public static unsafe object CheckCastAny(MethodTable* pTargetType, object obj)
		{
			if (obj != null && !AreTypesAssignable(obj.GetMethodTable(), pTargetType))
				ThrowHelper.ThrowInvalidCast();
			return obj;
		}

		public static unsafe bool IsInstanceOfException(MethodTable* pTargetType, object obj) => IsInstanceOfClass(pTargetType, obj) != null;

		public static unsafe void StelemRef(object[] array, nint index, object obj)
		{
			ref object element = ref Unsafe.As<ArrayElement[]>(array)[index].Value;
			if (obj != null)
			{
				MethodTable* elementType = array.GetMethodTable()->RelatedParameterType;
				MethodTable* objType = obj.GetMethodTable();
				if (elementType != objType && elementType != MethodTable.Of<object>() && !AreTypesAssignable(objType, elementType))
					ThrowHelper.ThrowArrayTypeMismatch();
			}
			element = obj;
		}

		public static unsafe ref object LdelemaRef(object[] array, nint index, MethodTable* elementType)
		{
			ref object element = ref Unsafe.As<ArrayElement[]>(array)[index].Value;
			if (array.GetMethodTable()->RelatedParameterType != elementType)
				ThrowHelper.ThrowArrayTypeMismatch();
			return ref element;
		}

		internal static unsafe bool AreTypesAssignable(MethodTable* source, MethodTable* target)
		{
			if (source == target)
				return true;

			if (target->IsInterface)
				return ImplementsInterface(source, target);

			if (target->IsArray)
			{
				if (!source->IsArray || source->IsSzArray != target->IsSzArray)
					return false;
				// Array covariance for reference element types; value type elements must match exactly
				MethodTable* sourceElement = source->RelatedParameterType;
				MethodTable* targetElement = target->RelatedParameterType;
				if (sourceElement == targetElement)
					return true;
				return !sourceElement->IsValueType && !targetElement->IsValueType && AreTypesAssignable(sourceElement, targetElement);
			}

			if (target->IsParameterizedType)
				return false;

			for (MethodTable* type = source->BaseType; type != null; type = type->BaseType)
			{
				if (type == target)
					return true;
			}
			return false;
		}

		// The interface map lists every interface the type implements, inherited ones included
		private static unsafe bool ImplementsInterface(MethodTable* source, MethodTable* target)
		{
			MethodTable** map = source->InterfaceMap;
			for (int i = 0; i < source->NumInterfaces; i++)
			{
				if (map[i] == target)
					return true;
			}

			if (target->HasGenericVariance || source->IsIDynamicInterfaceCastable)
				ThrowHelper.FailFast("FAILFAST: variant or dynamic interface casts are not supported");
			return false;
		}

		private struct ArrayElement
		{
			public object Value;
		}
	}

	// Boxing helpers for paths the JIT doesn't inline (Nullable<T>, shared generics)
	internal static class RuntimeExports
	{
		public static unsafe object RhBox(MethodTable* pEEType, ref byte data)
		{
			if (pEEType->IsNullable)
				ThrowHelper.FailFast("FAILFAST: boxing Nullable<T> is not supported");
			object box = Allocator.NewObject(pEEType);
			SpanHelpers.Memmove(ref box.GetRawData(), ref data, pEEType->ValueTypeSize);
			return box;
		}

		public static unsafe ref byte RhUnbox2(MethodTable* pUnboxToEEType, object obj)
		{
			if (obj == null || obj.GetMethodTable() != pUnboxToEEType)
				ThrowHelper.ThrowInvalidCast();
			return ref obj.GetRawData();
		}

		public static unsafe void RhUnboxNullable(ref byte data, MethodTable* pUnboxToEEType, object obj)
		{
			ThrowHelper.FailFast("FAILFAST: unboxing to Nullable<T> is not supported");
		}

		public static unsafe void RhUnboxTypeTest(MethodTable* pType, MethodTable* pBoxType)
		{
			if (pType != pBoxType)
				ThrowHelper.ThrowInvalidCast();
		}
	}
}

namespace System.Runtime.CompilerServices
{
	// Lazy cctors, single-threaded; re-entering a running cctor returns early, as in .NET
	internal static class ClassConstructorRunner
	{
		private static object CheckStaticClassConstructionReturnGCStaticBase(ref StaticClassConstructionContext context, object gcStaticBase)
		{
			CheckStaticClassConstruction(ref context);
			return gcStaticBase;
		}

		private static nint CheckStaticClassConstructionReturnNonGCStaticBase(ref StaticClassConstructionContext context, nint nonGcStaticBase)
		{
			CheckStaticClassConstruction(ref context);
			return nonGcStaticBase;
		}

		private static unsafe void CheckStaticClassConstruction(ref StaticClassConstructionContext context)
		{
			nint cctor = context.cctorMethodAddress;
			if (cctor == 0 || cctor == 1)
				return;
			context.cctorMethodAddress = 1;
			((delegate*<void>)cctor)();
			context.cctorMethodAddress = 0;
		}
	}
}
