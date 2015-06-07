﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Duality
{
	public static class ExtMethodsTypeInfo
	{
		/// <summary>
		/// Returns a TypeInfos BaseType as a TypeInfo, or null if it was null.
		/// </summary>
		/// <param name="type"></param>
		/// <returns></returns>
		public static TypeInfo GetBaseTypeInfo(this TypeInfo type)
		{
			return type.BaseType != null ? type.BaseType.GetTypeInfo() : null;
		}
		/// <summary>
		/// Returns a Types inheritance level. The <c>object</c>-Type has an inheritance level of
		/// zero, each subsequent inheritance increases it by one.
		/// </summary>
		/// <param name="t"></param>
		/// <returns></returns>
		public static int GetInheritanceDepth(this TypeInfo type)
		{
			int level = 0;
			while (type.BaseType != null)
			{
				type = type.BaseType.GetTypeInfo();
				level++;
			}
			return level;
		}

		/// <summary>
		/// Returns all fields that are declared within this Type, or any of its base Types.
		/// Includes public, non-public, static and instance fields.
		/// </summary>
		/// <param name="flags"></param>
		/// <returns></returns>
		public static IEnumerable<FieldInfo> DeclaredFieldsDeep(this TypeInfo type)
		{
			IEnumerable<FieldInfo> result = Enumerable.Empty<FieldInfo>();

			while (type != null)
			{
				result = result.Concat(type.DeclaredFields);
				type = type.GetBaseTypeInfo();
			}

			return result;
		}
	}
}
