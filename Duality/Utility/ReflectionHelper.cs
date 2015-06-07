﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq.Expressions;

using Duality.Serialization;

namespace Duality
{
	/// <summary>
	/// Provides reflection-related helper methods.
	/// </summary>
	public static class ReflectionHelper
	{
		private const char MemberTokenUndefined			= 'U';
		private const char MemberTokenTypeInfo			= 'T';
		private const char MemberTokenFieldInfo			= 'F';
		private const char MemberTokenPropertyInfo		= 'P';
		private const char MemberTokenEventInfo			= 'E';
		private const char MemberTokenMethodInfo		= 'M';
		private const char MemberTokenConstructorInfo	= 'C';

		private delegate object ObjectActivator();

		private	static	readonly	ObjectActivator			nullObjectActivator			= () => null;
		private	static	Dictionary<Type,ObjectActivator>	createInstanceMethodCache	= new Dictionary<Type,ObjectActivator>();
		private	static	Dictionary<string,Type>				typeResolveCache			= new Dictionary<string,Type>();
		private	static	Dictionary<string,MemberInfo>		memberResolveCache			= new Dictionary<string,MemberInfo>();
		private	static	Dictionary<Type,bool>				plainOldDataTypeCache		= new Dictionary<Type,bool>();
		private	static	Dictionary<MemberInfo,Attribute[]>	customMemberAttribCache		= new Dictionary<MemberInfo,Attribute[]>();
		private	static	Dictionary<KeyValuePair<Type,Type>,bool>	resRefCache			= new Dictionary<KeyValuePair<Type,Type>,bool>();

		/// <summary>
		/// Fired when automatically resolving a certain Type has failed. Allows any subscriber to provide a suitable match.
		/// </summary>
		public static event EventHandler<ResolveMemberEventArgs> TypeResolve	= null;
		/// <summary>
		/// Fired when automatically resolving a certain Member has failed. Allows any subscriber to provide a suitable match.
		/// </summary>
		public static event EventHandler<ResolveMemberEventArgs> MemberResolve	= null;

		/// <summary>
		/// Equals <c>BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic</c>.
		/// </summary>
		public const BindingFlags BindInstanceAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
		/// <summary>
		/// Equals <c>BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic</c>.
		/// </summary>
		public const BindingFlags BindStaticAll = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
		/// <summary>
		/// Equals <c>BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic</c>.
		/// </summary>
		public const BindingFlags BindAll = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

		/// <summary>
		/// Creates an instance of a Type. Attempts to use the Types default empty constructor, but will
		/// return an uninitialized object in case no constructor is available.
		/// </summary>
		/// <param name="type">The Type to create an instance of.</param>
		/// <returns>An instance of the Type. Null, if instanciation wasn't possible.</returns>
		[System.Diagnostics.DebuggerStepThrough]
		public static object CreateInstanceOf(this Type type)
		{
			ObjectActivator activator;
			if (createInstanceMethodCache.TryGetValue(type, out activator))
			{
				return activator();
			}
			else
			{
				// Filter out non-instantiatable Types
				if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition)
				{
					activator = nullObjectActivator;
				}
				// If the caller wants a string, just return an empty one
				else if (type == typeof(string))
				{
					activator = () => "";
				}
				// If the caller wants an array, create an empty one
				else if (typeof(Array).IsAssignableFrom(type) && type.GetArrayRank() == 1)
				{
					activator = () => Array.CreateInstance(type.GetElementType(), 0);
				}
				else
				{
					try
					{
						// Attempt to invoke the Type default empty constructor
						ConstructorInfo emptyConstructor = type.GetConstructor(BindInstanceAll, null, Type.EmptyTypes, null);
						if (emptyConstructor != null)
						{
							var constructorLambda = Expression.Lambda<ObjectActivator>(Expression.New(emptyConstructor));
							activator = constructorLambda.Compile();
						}
						// If there is no such constructor available, provide an uninitialized object
						else
						{
							activator = () => System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
						}
					}
					catch (Exception)
					{
						activator = nullObjectActivator;
					}
				}

				// Test whether our activation method really works, and mind it for later
				object firstResult;
				try
				{
					firstResult = activator();
				}
				catch (Exception e)
				{
					// If we fail to initialize the Type due to a problem in its static constructor, it's likely a user problem. Let him know.
					if (e is TypeInitializationException)
					{
						Log.Editor.WriteError("Failed to initialize Type {0}: {1}",
							Log.Type(type),
							Log.Exception(e.InnerException));
					}

					activator = nullObjectActivator;
					firstResult = null;
				}
				createInstanceMethodCache[type] = activator;

				return firstResult;
			}
		}
		/// <summary>
		/// Returns the default instance of a Type. Equals <c>default(T)</c>, but works for Reflection.
		/// </summary>
		/// <param name="instanceType">The Type to create a default instance of.</param>
		/// <returns></returns>
		public static object GetDefaultInstanceOf(this Type instanceType)
		{
			if (instanceType.IsValueType)
				return instanceType.CreateInstanceOf();
			else
				return null;
		}

		/// <summary>
		/// Returns whether two MemberInfo objects are equal.
		/// </summary>
		/// <param name="lhs">The first MemberInfo.</param>
		/// <param name="rhs">The second MemberInfo.</param>
		/// <returns></returns>
		public static bool MemberInfoEquals(MemberInfo lhs, MemberInfo rhs)
		{
			if (lhs == rhs)
				return true;
 
			if (lhs.DeclaringType != rhs.DeclaringType)
				return false;
 
			// Methods on arrays do not have metadata tokens but their ReflectedType
			// always equals their DeclaringType
			if (lhs.DeclaringType != null && lhs.DeclaringType.IsArray)
				return false;
 
			if (lhs.MetadataToken != rhs.MetadataToken || lhs.Module != rhs.Module)
				return false;
 
			if (lhs is MethodInfo)
			{
				MethodInfo lhsMethod = lhs as MethodInfo;
 
				if (lhsMethod.IsGenericMethod)
				{
					MethodInfo rhsMethod = rhs as MethodInfo;
 
					Type[] lhsGenArgs = lhsMethod.GetGenericArguments();
					Type[] rhsGenArgs = rhsMethod.GetGenericArguments();
					for (int i = 0; i < rhsGenArgs.Length; i++)
					{
						if (lhsGenArgs[i] != rhsGenArgs[i])
							return false;
					}
				}
			}
			return true;
		}

		/// <summary>
		/// Returns all custom attributes of the specified Type that are attached to the specified member.
		/// Inherites attributes are returned as well. This method is usually faster than <see cref="Attribute.GetCustomAttributes"/>,
		/// because it caches previous results internally.
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="member"></param>
		/// <returns></returns>
		public static IEnumerable<T> GetAttributesCached<T>(this MemberInfo member) where T : Attribute
		{
			Attribute[] result;
			if (!customMemberAttribCache.TryGetValue(member, out result))
			{
				result = Attribute.GetCustomAttributes(member, true);
				if (member.DeclaringType != null && !member.DeclaringType.IsInterface)
				{
					IEnumerable<Attribute> query = result;
					Type[] interfaces = member.DeclaringType.GetInterfaces();
					if (interfaces.Length > 0)
					{
						bool addedAny = false;
						foreach (Type interfaceType in interfaces)
						{
							MemberInfo[] interfaceMembers = interfaceType.GetMember(member.Name, member.MemberType, ReflectionHelper.BindInstanceAll);
							foreach (MemberInfo interfaceMemberInfo in interfaceMembers)
							{
								IEnumerable<Attribute> subQuery = GetAttributesCached<Attribute>(interfaceMemberInfo);
								if (subQuery.Any())
								{
									query = query.Concat(subQuery);
									addedAny = true;
								}
							}
						}
						if (addedAny)
						{
							result = query.Distinct().ToArray();
						}
					}
				}
				customMemberAttribCache[member] = result;
			}

			if (typeof(T) == typeof(Attribute))
				return result as IEnumerable<T>;
			else
				return result.OfType<T>();
		}
		/// <summary>
		/// Returns all custom attributes of the specified Type that are attached to the specified member.
		/// Inherites attributes are returned as well. This method is usually faster than <see cref="Attribute.GetCustomAttributes"/>
		/// and similar .Net methods, because it caches previous results internally.
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="member"></param>
		/// <returns></returns>
		public static bool HasAttributeCached<T>(this MemberInfo member) where T : Attribute
		{
			return GetAttributesCached<T>(member).Any();
		}

		/// <summary>
		/// Visits all fields of an object and all its sub-objects in order to find and / or adjust values of a certain Type. This is likely to be a very expensive operation.
		/// </summary>
		/// <typeparam name="T">The value type that is searched for.</typeparam>
		/// <param name="obj">The root object where the search begins.</param>
		/// <param name="visitor">An object field visitor. Returns a new value for the visited object.</param>
		/// <param name="stopAtTarget">If true, visited objects of the target type won't be investigated deeply. If false, even target objects are unwrapped in order to check their sub-objects.</param>
		public static void VisitObjectsDeep<T>(object obj, Func<T,T> visitor, bool stopAtTarget = true)
		{
			VisitObjectsDeep<T>(obj, visitor, stopAtTarget, new HashSet<object>(), new Dictionary<Type,bool>());
		}
		private static object VisitObjectsDeep<T>(object obj, Func<T,T> visitor, bool stopAtTarget, HashSet<object> visitedGraph, Dictionary<Type,bool> exploreTypeCache)
		{
			if (obj == null) return obj;
			if (visitedGraph.Contains(obj)) return obj;
			TypeInfo objType = obj.GetType().GetTypeInfo();

			// Visit object
			if (objType.IsClass) visitedGraph.Add(obj);
			if (obj is T)
			{
				obj = visitor((T)obj);
				if (stopAtTarget) return obj;
			}

			// Check if object type should be explored
			bool explore = false;
			if (!exploreTypeCache.TryGetValue(objType, out explore))
			{
				explore = VisitObjectsDeep_ExploreType(typeof(T), objType, exploreTypeCache);
				exploreTypeCache[objType] = explore;
			}
			if (!explore) return obj;

			// Arrays
			if (objType.IsArray)
			{
				Array baseArray = (Array)obj;
				Type elemType = objType.GetElementType();
				int length = baseArray.Length;

				// Explore elements
				if (!elemType.IsClass || typeof(T).IsAssignableFrom(elemType))
				{
					for (int i = 0; i < length; ++i)
					{
						object val = baseArray.GetValue(i);
						val = VisitObjectsDeep<T>(val, visitor, stopAtTarget, visitedGraph, exploreTypeCache);
						baseArray.SetValue(val, i);
					}
				}
				else
				{
					for (int i = 0; i < length; ++i)
					{
						object val = baseArray.GetValue(i);
						VisitObjectsDeep<T>(val, visitor, stopAtTarget, visitedGraph, exploreTypeCache);
					}
				}
			}
			// Complex objects
			else
			{
				// Explore fields
				var fields = objType.DeclaredFieldsDeep();
				foreach (FieldInfo field in fields)
				{
					if (field.IsStatic) continue;

					object val = field.GetValue(obj);
					val = VisitObjectsDeep<T>(val, visitor, stopAtTarget, visitedGraph, exploreTypeCache);
					if (!objType.IsClass || typeof(T).IsAssignableFrom(field.FieldType)) field.SetValue(obj, val);
				}
			}

			return obj;
		}
		private static bool VisitObjectsDeep_ExploreType(Type searchForType, Type variableType, Dictionary<Type,bool> exploreTypeCache)
		{
			// Check the cache and calculate the value, if we missed
			bool explore = false;
			if (!exploreTypeCache.TryGetValue(variableType, out explore))
			{
				// Found a variable of the searched type? Done.
				if (searchForType.IsAssignableFrom(variableType))
					explore = true;

				// Don't explore primitives
				else if (variableType.IsPrimitive)
					explore = false;
				else if (variableType.IsEnum)
					explore = false;

				// Some hardcoded early-outs for well known types
				else if (variableType == typeof(decimal))
					explore = false;
				else if (variableType == typeof(string))
					explore = false;
				else if (typeof(MemberInfo).IsAssignableFrom(variableType))
					explore = false;
				else if (typeof(Delegate).IsAssignableFrom(variableType))
					explore = false;

				// We also need to explore (for example) all "object" variables, because they could contain anything
				else if (variableType.IsAssignableFrom(searchForType))
					explore = true;

				// Perform a deep check unless trivially true
				else
				{
					// Don't get trapped in circles: Assume false, until we found out otherwise
					exploreTypeCache[variableType] = explore;
				
					// Check element type
					if (variableType.IsArray)
					{
						explore = VisitObjectsDeep_ExploreType(searchForType, variableType.GetElementType(), exploreTypeCache);
					}
					// Check referred fields
					else
					{
						explore = variableType.GetTypeInfo().DeclaredFieldsDeep().Any(f => 
							!f.IsStatic &&
							!string.Equals(f.Name, "_syncRoot", StringComparison.InvariantCultureIgnoreCase) && 
							VisitObjectsDeep_ExploreType(searchForType, f.FieldType, exploreTypeCache));
					}
				}
				exploreTypeCache[variableType] = explore;
			}
			return explore;
		}

		/// <summary>
		/// Cleans the specified object instance from event bindings that lead to a specific invalid Assembly.
		/// This method is used to sweep forgotten event bindings upon plugin reload.
		/// </summary>
		/// <param name="targetObject"></param>
		/// <param name="invalidAssembly"></param>
		/// <returns>Returns true, if any event binding was removed.</returns>
		public static bool CleanEventBindings(object targetObject, Assembly invalidAssembly)
		{
			bool cleanedAny = false;
			TypeInfo targetType = targetObject.GetType().GetTypeInfo();
			IEnumerable<FieldInfo> targetFields = targetType.DeclaredFieldsDeep();
			foreach (FieldInfo field in targetFields)
			{
				if (field.IsStatic) continue;
				if (typeof(Delegate).IsAssignableFrom(field.FieldType))
				{
					Delegate delOld = field.GetValue(targetObject) as Delegate;
					Delegate delNew = CleanEventBindings_Delegate(delOld, invalidAssembly);
					if (!object.ReferenceEquals(delNew, delOld))
					{
						cleanedAny = true;
						field.SetValue(targetObject, delNew);
					}
				}
			}
			return cleanedAny;
		}
		/// <summary>
		/// Cleans the specified class from static event bindings that lead to a specific invalid Assembly.
		/// This method is used to sweep forgotten event bindings upon plugin reload.
		/// </summary>
		/// <param name="targetType"></param>
		/// <param name="invalidAssembly"></param>
		/// <returns>Returns true, if any event binding was removed.</returns>
		public static bool CleanEventBindings(Type targetType, Assembly invalidAssembly)
		{
			bool cleanedAny = false;
			IEnumerable<FieldInfo> targetFields = targetType.GetTypeInfo().DeclaredFieldsDeep();
			foreach (FieldInfo field in targetFields)
			{
				if (!field.IsStatic) continue;
				if (typeof(Delegate).IsAssignableFrom(field.FieldType))
				{
					Delegate delOld = field.GetValue(null) as Delegate;
					Delegate delNew = CleanEventBindings_Delegate(delOld, invalidAssembly);
					if (!object.ReferenceEquals(delNew, delOld))
					{
						cleanedAny = true;
						field.SetValue(null, delNew);
					}
				}
			}
			return cleanedAny;
		}
		private static Delegate CleanEventBindings_Delegate(Delegate del, Assembly invalidAssembly)
		{
			if (del == null) return del;
			Delegate[] oldInvokeList = del.GetInvocationList();
			if (oldInvokeList.Any(e => e.Method.DeclaringType.Assembly == invalidAssembly))
			{
				return Delegate.Combine(oldInvokeList.Where(e => e.Method.DeclaringType.Assembly != invalidAssembly).ToArray());
			}
			return del;
		}

		/// <summary>
		/// Returns whether a certain <see cref="Duality.Resource"/> Type is able to reference another specific <see cref="Duality.Resource"/> Type.
		/// This is a pure optimization method that doesn't guarantee exact information in all cases - returns true, when in doubt.
		/// </summary>
		/// <param name="sourceResType"></param>
		/// <param name="targetResType"></param>
		/// <returns></returns>
		public static bool CanReferenceResource(Type sourceResType, Type targetResType)
		{
			if (!typeof(Resource).IsAssignableFrom(sourceResType)) throw new ArgumentException("Only Resource Types are valid.", "sourceResType");
			if (!typeof(Resource).IsAssignableFrom(targetResType)) throw new ArgumentException("Only Resource Types are valid.", "targetResType");
			
			bool result;
			if (!resRefCache.TryGetValue(new KeyValuePair<Type,Type>(sourceResType, targetResType), out result))
			{
				resRefCache[new KeyValuePair<Type,Type>(sourceResType, targetResType)] = false;

				bool foundIt = false;
				bool foundAny = false;
				foreach (ExplicitResourceReferenceAttribute refAttrib in GetAttributesCached<ExplicitResourceReferenceAttribute>(sourceResType))
				{
					foundAny = true;
					foreach (Type refType in refAttrib.ReferencedTypes)
					{
						if (refType.IsAssignableFrom(targetResType) || CanReferenceResource(refType, targetResType))
						{
							foundIt = true;
							break;
						}
					}
					if (foundIt) break;
				}

				result = foundIt || !foundAny;
				resRefCache[new KeyValuePair<Type,Type>(sourceResType, targetResType)] = result;
			}

			return result;
		}

		/// <summary>
		/// Returns whether the specified type is a primitive, enum, string, decimal, or struct that
		/// consists only of those types.
		/// </summary>
		/// <param name="baseObj"></param>
		/// <returns></returns>
		public static bool IsPlainOldData(this Type type)
		{
			// Early-out for some obvious cases
			if (type.IsArray) return false;
			if (type.IsPrimitive) return true;
			if (type.IsEnum) return true;

			// Special cases for some well-known classes
			if (type == typeof(string)) return true;
			if (type == typeof(decimal)) return true;

			// Otherwise, any class is not plain old data
			if (type.IsClass) return false;

			// If we have no evidence so far, check the cache and iterate fields
			bool isPlainOldData;
			if (plainOldDataTypeCache.TryGetValue(type, out isPlainOldData))
			{
				return isPlainOldData;
			}
			else
			{
				isPlainOldData = true;
				foreach (FieldInfo field in type.GetTypeInfo().DeclaredFieldsDeep())
				{
					if (field.IsStatic) continue;
					if (!IsPlainOldData(field.FieldType))
					{
						isPlainOldData = false;
						break;
					}
				}
				plainOldDataTypeCache[type] = isPlainOldData;
				return isPlainOldData;
			}
		}

		/// <summary>
		/// Resolves a Type based on its <see cref="GetTypeId">type id</see>.
		/// </summary>
		/// <param name="typeString">The type string to resolve.</param>
		/// <param name="declaringMethod">The generic method that is declaring the Type. Only necessary when resolving a generic methods parameter Type.</param>
		/// <returns></returns>
		public static Type ResolveType(string typeString, MethodInfo declaringMethod = null)
		{
			return ResolveType(typeString, null, declaringMethod);
		}
		/// <summary>
		/// Resolves a Member based on its <see cref="GetMemberId">member id</see>.
		/// </summary>
		/// <param name="memberString">The <see cref="GetMemberId">member id</see> of the member.</param>
		/// <param name="throwOnError">If true, an Exception is thrown on failure.</param>
		/// <returns></returns>
		public static MemberInfo ResolveMember(string memberString)
		{
			MemberInfo result;
			if (memberResolveCache.TryGetValue(memberString, out result)) return result;

			Assembly[] searchAsm = AppDomain.CurrentDomain.GetAssemblies().Except(DualityApp.DisposedPlugins).ToArray();
			result = FindMember(memberString, searchAsm);
			if (result != null) memberResolveCache[memberString] = result;

			return result;
		}
		
		/// <summary>
		/// Returns the short version of an Assembly name.
		/// </summary>
		/// <param name="assembly"></param>
		/// <returns></returns>
		public static string GetShortAssemblyName(this Assembly assembly)
		{
			return assembly.FullName.Split(',')[0];
		}
		/// <summary>
		/// Returns the short version of an Assembly name.
		/// </summary>
		/// <param name="assemblyName"></param>
		/// <returns></returns>
		public static string GetShortAssemblyName(this AssemblyName assemblyName)
		{
			return assemblyName.FullName.Split(',')[0];
		}
		/// <summary>
		/// Returns the short version of an Assembly name.
		/// </summary>
		/// <param name="assemblyName"></param>
		/// <returns></returns>
		public static string GetShortAssemblyName(string assemblyName)
		{
			return assemblyName.Split(',')[0];
		}

		/// <summary>
		/// Returns a string describing a certain Type.
		/// </summary>
		/// <param name="T">The Type to describe</param>
		/// <returns></returns>
		public static string GetTypeCSCodeName(this Type T, bool shortName = false)
		{
			StringBuilder typeStr = new StringBuilder();

			if (T.IsGenericParameter)
			{
				return T.Name;
			}
			if (T.IsArray)
			{
				typeStr.Append(GetTypeCSCodeName(T.GetElementType(), shortName));
				typeStr.Append('[');
				typeStr.Append(',', T.GetArrayRank() - 1);
				typeStr.Append(']');
			}
			else
			{
				Type[] genArgs = T.IsGenericType ? T.GetGenericArguments() : null;

				if (T.IsNested)
				{
					Type declType = T.DeclaringType;
					if (declType.IsGenericTypeDefinition)
					{
						Array.Resize(ref genArgs, declType.GetGenericArguments().Length);
						declType = declType.MakeGenericType(genArgs);
						genArgs = T.GetGenericArguments().Skip(genArgs.Length).ToArray();
					}
					string parentName = GetTypeCSCodeName(declType, shortName);

					string[] nestedNameToken = shortName ? T.Name.Split('+') : T.FullName.Split('+');
					string nestedName = nestedNameToken[nestedNameToken.Length - 1];
						
					int genTypeSepIndex = nestedName.IndexOf("[[", StringComparison.Ordinal);
					if (genTypeSepIndex != -1) nestedName = nestedName.Substring(0, genTypeSepIndex);
					genTypeSepIndex = nestedName.IndexOf('`');
					if (genTypeSepIndex != -1) nestedName = nestedName.Substring(0, genTypeSepIndex);

					typeStr.Append(parentName);
					typeStr.Append('.');
					typeStr.Append(nestedName);
				}
				else
				{
					if (shortName)
						typeStr.Append(T.Name.Split(new[] {'`'}, StringSplitOptions.RemoveEmptyEntries)[0].Replace('+', '.'));
					else
						typeStr.Append(T.FullName.Split(new[] {'`'}, StringSplitOptions.RemoveEmptyEntries)[0].Replace('+', '.'));
				}

				if (genArgs != null && genArgs.Length > 0)
				{
					if (T.IsGenericTypeDefinition)
					{
						typeStr.Append('<');
						typeStr.Append(',', genArgs.Length - 1);
						typeStr.Append('>');
					}
					else if (T.IsGenericType)
					{
						typeStr.Append('<');
						for (int i = 0; i < genArgs.Length; i++)
						{
							typeStr.Append(GetTypeCSCodeName(genArgs[i], shortName));
							if (i < genArgs.Length - 1)
								typeStr.Append(',');
						}
						typeStr.Append('>');
					}
				}
			}

			return typeStr.Replace('+', '.').ToString();
		}
		/// <summary>
		/// Returns a string describing a certain Type.
		/// </summary>
		/// <param name="T">The Type to describe</param>
		/// <returns></returns>
		public static string GetTypeId(this Type T)
		{
			return T.FullName != null ? Regex.Replace(T.FullName, @"(, [^\]\[]*)", "") : T.Name;
		}
		/// <summary>
		/// Returns a string describing a certain Member of a Type.
		/// </summary>
		/// <param name="member">The Member to describe.</param>
		/// <returns></returns>
		public static string GetMemberId(this MemberInfo member)
		{
			if (member is Type)
				return MemberTokenTypeInfo + ":" + GetTypeId(member as Type);
			if (member is TypeInfo)
				return MemberTokenTypeInfo + ":" + GetTypeId((member as TypeInfo).AsType());

			string declType = member.DeclaringType.GetTypeId();

			FieldInfo field = member as FieldInfo;
			if (field != null)
				return MemberTokenFieldInfo + ":" + declType + ':' + field.Name;

			EventInfo ev = member as EventInfo;
			if (ev != null)
				return MemberTokenEventInfo + ":" + declType + ':' + ev.Name;

			PropertyInfo property = member as PropertyInfo;
			if (property != null)
			{
				ParameterInfo[] parameters = property.GetIndexParameters();
				if (parameters.Length == 0)
					return MemberTokenPropertyInfo + ":" + declType + ':' + property.Name;
				else
					return MemberTokenPropertyInfo + ":" + declType + ':' + property.Name + '(' + parameters.ToString(p => p.ParameterType.GetTypeId(), ",") + ')';
			}

			ConstructorInfo ctor = member as ConstructorInfo;
			if (ctor != null)
			{
				ParameterInfo[] parameters = ctor.GetParameters();

				string result = MemberTokenConstructorInfo + ":" + declType + ':' + (ctor.IsStatic ? "s" : "i");

				if (parameters.Length != 0)
					result += '(' + parameters.ToString(p => p.ParameterType.GetTypeId(), ", ") + ')';

				return result;
			}

			MethodInfo method = member as MethodInfo;
			if (method != null)
			{
				ParameterInfo[] parameters = method.GetParameters();
				Type[] genArgs = method.GetGenericArguments();

				string result = MemberTokenMethodInfo + ":" + declType + ':' + method.Name;
				
				if (genArgs.Length != 0)
				{
					if (method.IsGenericMethodDefinition)
						result += "``" + genArgs.Length.ToString(CultureInfo.InvariantCulture);
					else
						result += "``" + genArgs.Length.ToString(CultureInfo.InvariantCulture) + '[' + genArgs.ToString(t => "[" + t.GetTypeId() + "]", ",") + ']';
				}
				if (parameters.Length != 0)
					result += '(' + parameters.ToString(p => p.ParameterType.GetTypeId(), ",") + ')';

				return result;
			}

			throw new NotSupportedException(string.Format("Member Type '{0} not supported", Log.Type(member.GetType())));
		}

		/// <summary>
		/// Performs a selective split operation on the specified string. Intended to be used on hierarchial argument lists.
		/// </summary>
		/// <param name="argList">The argument list to split.</param>
		/// <param name="pushLevel">The char that increases the current hierarchy level.</param>
		/// <param name="popLevel">The char that decreases the current hierarchy level.</param>
		/// <param name="separator">The char that separates two arguments.</param>
		/// <param name="splitLevel">The hierarchy level at which to perform the split operation.</param>
		/// <returns></returns>
		public static string[] SplitArgs(string argList, char pushLevel, char popLevel, char separator, int splitLevel)
		{
			if (argList == null) return new string[0];

			// Splitting the parameter list without destroying generic arguments
			int lastSplitIndex = -1;
			int genArgLevel = 0;
			List<string> ptm = new List<string>();
			for (int i = 0; i < argList.Length; i++)
			{
				if (argList[i] == separator && genArgLevel == splitLevel)
				{
					ptm.Add(argList.Substring(lastSplitIndex + 1, i - lastSplitIndex - 1));
					lastSplitIndex = i;
				}
				else if (argList[i] == pushLevel)
					genArgLevel++;
				else if (argList[i] == popLevel)
					genArgLevel--;
			}
			ptm.Add(argList.Substring(lastSplitIndex + 1, argList.Length - lastSplitIndex - 1));
			return ptm.ToArray();
		}
		
		/// <summary>
		/// Clears the ReflectionHelpers Type cache.
		/// </summary>
		internal static void ClearTypeCache()
		{
			createInstanceMethodCache.Clear();
			typeResolveCache.Clear();
			memberResolveCache.Clear();
			plainOldDataTypeCache.Clear();
			resRefCache.Clear();
			customMemberAttribCache.Clear();
		}

		private static Type ResolveType(string typeString, IEnumerable<Assembly> searchAsm, MethodInfo declaringMethod)
		{
			if (typeString == null) return null;

			Type result;
			if (typeResolveCache.TryGetValue(typeString, out result)) return result;

			if (searchAsm == null) searchAsm = AppDomain.CurrentDomain.GetAssemblies().Except(DualityApp.DisposedPlugins).ToArray();
			result = FindType(typeString, searchAsm, declaringMethod);
			if (result != null && declaringMethod == null) typeResolveCache[typeString] = result;

			return result;
		}
		private static Type FindType(string typeName, IEnumerable<Assembly> asmSearch, MethodInfo declaringMethod = null)
		{
			typeName = typeName.Trim();
			if (string.IsNullOrEmpty(typeName)) return null;

			// Retrieve generic parameters
			Match genericParamsMatch = Regex.Match(typeName, @"(\[)(\[.+\])(\])");
			string[] genericParams = null;
			if (genericParamsMatch.Groups.Count > 3)
			{
				string genericParamList = genericParamsMatch.Groups[2].Value;
				genericParams = SplitArgs(genericParamList, '[', ']', ',', 0);
				for (int i = 0; i < genericParams.Length; i++)
					genericParams[i] = genericParams[i].Substring(1, genericParams[i].Length - 2);
			}

			// Process type name
			string[] token = Regex.Split(typeName, @"\[\[.+\]\]");
			string typeNameBase = token[0];
			string elementTypeName = typeName;

			// Handle Reference
			if (token[token.Length - 1].LastOrDefault() == '&')
			{
				Type elementType = ResolveType(typeName.Substring(0, typeName.Length - 1), asmSearch, declaringMethod);
				if (elementType == null) return null;
				return elementType.MakeByRefType();
			}

			// Retrieve array ranks
			int arrayRank = 0;
			MatchCollection arrayMatches = Regex.Matches(token[token.Length - 1], @"\[,*\]");
			if (arrayMatches.Count > 0)
			{
				string rankStr = arrayMatches[arrayMatches.Count - 1].Value;
				arrayRank = 1 + rankStr.Count(c => c == ',');
				elementTypeName = elementTypeName.Substring(0, elementTypeName.Length - rankStr.Length);
			}
			
			// Handle Arrays
			if (arrayRank > 0)
			{
				Type elementType = ResolveType(elementTypeName, asmSearch, declaringMethod);
				if (elementType == null) return null;
				return arrayRank == 1 ? elementType.MakeArrayType() : elementType.MakeArrayType(arrayRank);
			}

			Type baseType = null;
			// Generic method parameter Types
			if (typeNameBase.StartsWith("``"))
			{
				if (declaringMethod != null)
				{
					int methodGenArgIndex = int.Parse(typeNameBase.Substring(2, typeNameBase.Length - 2));
					baseType = declaringMethod.GetGenericArguments()[methodGenArgIndex];
				}
			}
			else
			{
				// Retrieve base type
				foreach (Assembly a in asmSearch)
				{
					baseType = a.GetType(typeNameBase);
					if (baseType != null) break;
				}
				// Failed to retrieve base type? Try manually and ignore plus / dot difference.
				if (baseType == null)
				{
					string assemblyNameGuess = typeName.Split('.', '+').FirstOrDefault();
					IEnumerable<Assembly> sortedAsmSearch = asmSearch.OrderByDescending(a => a.GetShortAssemblyName() == assemblyNameGuess);
					foreach (Assembly a in sortedAsmSearch)
					{
						// Try to retrieve all Types from the current Assembly
						Type[] types;
						try { types = a.GetTypes(); }
						catch (Exception) { continue; }

						// Iterate and manually compare names
						foreach (Type t in types)
						{
							if (IsFullTypeNameEqual(typeNameBase, t.FullName))
							{
								baseType = t;
								break;
							}
						}
						if (baseType != null) break;
					}
				}
				// Failed anyway? Try explicit resolve
				if (baseType == null)
				{
					ResolveMemberEventArgs args = new ResolveMemberEventArgs(typeNameBase);
					if (TypeResolve != null) TypeResolve(null, args);
					baseType = args.ResolvedMember as Type;
				}
			}
			
			// Retrieve generic type params
			if (genericParams != null)
			{
				Type[] genericParamTypes = new Type[genericParams.Length];

				for (int i = 0; i < genericParamTypes.Length; i++)
				{
					// Explicitly referring to generic type definition params: Don't attemp to make it generic.
					if ((genericParams[i].Length > 0 && genericParams[i][0] == '`') && 
						(genericParams[i].Length < 2 || genericParams[i][1] != '`')) return baseType;

					genericParamTypes[i] = ResolveType(genericParams[i], asmSearch, declaringMethod);

					// Can't find the generic type argument: Fail.
					if (genericParamTypes[i] == null) return null;
				}

				if (baseType == null)
					return null;
				else
					return baseType.MakeGenericType(genericParamTypes);
			}

			return baseType;
		}
		private static MemberInfo FindMember(string memberString, IEnumerable<Assembly> asmSearch)
		{
			string[] token = memberString.Split(':');

			TypeInfo declaringType = token.Length > 1 ? ResolveType(token[1], asmSearch, null).GetTypeInfo() : null;
			char memberTypeToken = (token.Length > 0 && token[0].Length > 0) ? token[0][0] : MemberTokenUndefined;

			if (declaringType != null && memberTypeToken != ' ')
			{
				if (memberTypeToken == MemberTokenTypeInfo)
				{
					return declaringType;
				}
				else if (memberTypeToken == MemberTokenFieldInfo)
				{
					MemberInfo member = declaringType.GetField(token[2], BindAll);
					if (member != null) return member;
				}
				else if (memberTypeToken == MemberTokenEventInfo)
				{
					MemberInfo member = declaringType.GetEvent(token[2], BindAll);
					if (member != null) return member;
				}
				else
				{
					int memberParamListStartIndex = token[2].IndexOf('(');
					int memberParamListEndIndex = token[2].IndexOf(')');
					string memberParamList = memberParamListStartIndex != -1 ? token[2].Substring(memberParamListStartIndex + 1, memberParamListEndIndex - memberParamListStartIndex - 1) : null;
					string[] memberParams = SplitArgs(memberParamList, '[', ']', ',', 0);
					string memberName = memberParamListStartIndex != -1 ? token[2].Substring(0, memberParamListStartIndex) : token[2];
					Type[] memberParamTypes = memberParams.Select(p => ResolveType(p, asmSearch, null)).ToArray();

					if (memberTypeToken == MemberTokenConstructorInfo)
					{
						ConstructorInfo[] availCtors = declaringType.GetConstructors(memberName == "s" ? BindStaticAll : BindInstanceAll).Where(
							m => m.GetParameters().Length == memberParams.Length).ToArray();
						foreach (ConstructorInfo ctor in availCtors)
						{
							bool possibleMatch = true;
							ParameterInfo[] methodParams = ctor.GetParameters();
							for (int i = 0; i < methodParams.Length; i++)
							{
								string methodParamTypeName = methodParams[i].ParameterType.Name;
								if (methodParams[i].ParameterType != memberParamTypes[i] && methodParamTypeName != memberParams[i])
								{
									possibleMatch = false;
									break;
								}
							}
							if (possibleMatch) return ctor;
						}
					}
					else if (memberTypeToken == MemberTokenPropertyInfo)
					{
						PropertyInfo[] availProps = declaringType.GetProperties(BindAll).Where(
							m => m.Name == memberName && 
							m.GetIndexParameters().Length == memberParams.Length).ToArray();
						foreach (PropertyInfo prop in availProps)
						{
							bool possibleMatch = true;
							ParameterInfo[] methodParams = prop.GetIndexParameters();
							for (int i = 0; i < methodParams.Length; i++)
							{
								string methodParamTypeName = methodParams[i].ParameterType.Name;
								if (methodParams[i].ParameterType != memberParamTypes[i] && methodParamTypeName != memberParams[i])
								{
									possibleMatch = false;
									break;
								}
							}
							if (possibleMatch) return prop;
						}
					}

					int genArgTokenStartIndex = token[2].IndexOf("``", StringComparison.Ordinal);
					int genArgTokenEndIndex = memberParamListStartIndex != -1 ? memberParamListStartIndex : token[2].Length;
					string genArgToken = genArgTokenStartIndex != -1 ? token[2].Substring(genArgTokenStartIndex + 2, genArgTokenEndIndex - genArgTokenStartIndex - 2) : "";
					if (genArgTokenStartIndex != -1) memberName = token[2].Substring(0, genArgTokenStartIndex);			

					int genArgListStartIndex = genArgToken.IndexOf('[');
					int genArgListEndIndex = genArgToken.LastIndexOf(']');
					string genArgList = genArgListStartIndex != -1 ? genArgToken.Substring(genArgListStartIndex + 1, genArgListEndIndex - genArgListStartIndex - 1) : null;
					string[] genArgs = SplitArgs(genArgList, '[', ']', ',', 0);
					for (int i = 0; i < genArgs.Length; i++) genArgs[i] = genArgs[i].Substring(1, genArgs[i].Length - 2);

					int genArgCount = genArgToken.Length > 0 ? int.Parse(genArgToken.Substring(0, genArgListStartIndex != -1 ? genArgListStartIndex : genArgToken.Length)) : 0;

					// Select the method that fits
					MethodInfo[] availMethods = declaringType.GetRuntimeMethods().Where(m => 
						m.Name == memberName && 
						m.GetGenericArguments().Length == genArgCount &&
						m.GetParameters().Length == memberParams.Length).ToArray();
					foreach (MethodInfo method in availMethods)
					{
						bool possibleMatch = true;
						ParameterInfo[] methodParams = method.GetParameters();
						for (int i = 0; i < methodParams.Length; i++)
						{
							string methodParamTypeName = methodParams[i].ParameterType.Name;
							if (methodParams[i].ParameterType != memberParamTypes[i] && methodParamTypeName != memberParams[i])
							{
								possibleMatch = false;
								break;
							}
						}
						if (possibleMatch) return method;
					}
				}
			}
			
			// Failed? Try explicit resolve
			ResolveMemberEventArgs args = new ResolveMemberEventArgs(memberString);
			if (MemberResolve != null) MemberResolve(null, args);
			return args.ResolvedMember;
		}
		private static bool IsFullTypeNameEqual(string typeNameA, string typeNameB)
		{
			// Not doing this for performance reasons:
			//string nameTemp = typeNameA.Replace('+', '.');
			//if (typeNameB == nameTemp)

			if (typeNameA.Length != typeNameB.Length) return false;

			for (int i = 0; i < typeNameA.Length; ++i)
			{
				if (typeNameA[i] != typeNameB[i])
				{
					if (typeNameA[i] == '.' && typeNameB[i] == '+')
						continue;
					if (typeNameA[i] == '+' && typeNameB[i] == '.')
						continue;
					return false;
				}
			}

			return true;
		}
	}

	public class ResolveMemberEventArgs : EventArgs
	{
		private string memberId = null;
		private MemberInfo resolvedMember = null;

		/// <summary>
		/// [GET] The Member id to resolve.
		/// </summary>
		public string MemberId
		{
			get { return this.memberId; }
		}
		/// <summary>
		/// [GET / SET] The resolved Member.
		/// </summary>
		public MemberInfo ResolvedMember
		{
			get { return this.resolvedMember; }
			set { this.resolvedMember = value; }
		}

		public ResolveMemberEventArgs(string memberId)
		{
			this.memberId = memberId;
		}
	}
}
