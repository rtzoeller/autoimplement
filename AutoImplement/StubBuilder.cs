﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace AutoImplement {
   /// <summary>
   /// Automatic Stub implementations make use of C#'s 'explicit interface implementation' feature.
   /// Mutable members with the interface's member's names are added to the type.
   /// Then the interface is implemented with exlicit implementations that point back to those mutable members.
   /// The result is a type that can be changed at whim, but looks identical to the interface it's implementing.
   /// </summary>
   public class StubBuilder : IPatternBuilder {
      private readonly List<string> implementedMethods = new List<string>();

      private readonly StringWriter writer;

      public StubBuilder(StringWriter writer) => this.writer = writer;

      public string ClassDeclaration(Type interfaceType) {
         var interfaceName = interfaceType.CreateCsName(interfaceType.Namespace);
         var (basename, genericInfo) = interfaceName.ExtractImplementingNameParts();

         return $"Stub{basename}{genericInfo} : {interfaceName}";
      }

      public void AppendExtraMembers(Type interfaceType) { }

      /// <example>
      // public Func<int, int, int> Max { get; set; }
      // int ICalculator.Max(int a, int b)
      // {
      //    if (Max != null)
      //    {
      //       return this.Max(a, b);
      //    }
      //    else
      //    {
      //       return default(int);
      //    }
      // }
      // 
      // public Func<double, double, double> Max_double_double { get; set; }
      // double ICalculator.Max(double a, double b)
      // {
      //    if (Max_double_double != null)
      //    {
      //       return this.Max_double_double(a, b);
      //    }
      //    else
      //    {
      //       return default(double);
      //    }
      // }
      /// </example>
      /// <remarks>
      /// Methods in interfaces are replaced with delegate properties.
      /// Assigning one of those delegates a value will change the behavior of that method.
      /// You can call the delegate just like the method.
      /// When the interface method is called, it will call the delegate method if possible.
      /// If there is no delegate, it returns default.
      /// </remarks>
      public void AppendMethod(MethodInfo info, MemberMetadata method) {
         var delegateName = GetStubName(method.ReturnType, method.ParameterTypes);
         var typesExtension = SanitizeMethodName(method.ParameterTypes);

         var methodsWithMatchingNameButNotSignature = implementedMethods.Where(name => name.Split('(')[0] == method.Name && name != $"{method.Name}({method.ParameterTypes})");
         string localImplementationName = methodsWithMatchingNameButNotSignature.Any() ? $"{method.Name}_{typesExtension}" : method.Name;

         if (info.GetParameters().Any(p => p.ParameterType.IsByRef)) {
            localImplementationName = $"{method.Name}_{typesExtension}";
            delegateName = $"{method.Name}Delegate_{typesExtension}";
            writer.Write($"public delegate {method.ReturnType} {delegateName}({method.ParameterTypesAndNames});" + Environment.NewLine);
         }

         // only add a delegation property for the first method with a given signature
         // this is important for IEnumerable<T>.GetEnumerator() and IEnumerable.GetEnumerator() -> same name, same signature
         if (!implementedMethods.Any(name => name == $"{method.Name}({method.ParameterTypes})")) {
            writer.Write($"public {delegateName} {localImplementationName} {{ get; set; }}" + Environment.NewLine);
         }

         ImplementInterfaceMethod(info, localImplementationName, method);
         implementedMethods.Add($"{method.Name}({method.ParameterTypes})");
      }

      /// <example>
      // public EventImplementation<EventArgs> ValueChanged = new EventImplementation<EventHandler>();
      // 
      // event EventHandler INotifyValueChanged.ValueChanged
      // {
      //    add
      //    {
      //       ValueChanged.add(new System.EventHandler<EventArgs>(value));
      //    }
      //    remove
      //    {
      //       ValueChanged.remove(new System.EventHandler<EventArgs>(value));
      //    }
      // }
      /// </example>
      /// <remarks>
      /// Events are replaces with an EventImplementation field.
      /// Explicit interface implementations then call that EventImplementation.
      /// 
      /// EventImplementation exposes add, remove, handlers, and +/- operators along with an Invoke method.
      /// This allows you to assign custom add/remove handlers to the Stub, or make decision based on the individual added handlers,
      /// or use +=, -=, and .Invoke as if the EventImplementation were actually an event.
      /// 
      // Note that the explicit implementation always casts added/removed delegates to EventHandler<T>.
      // This is to avoid having to deal with .net's 2 types of EventHandlers separately.
      // Example: RoutedEventHandler vs EventHandler<RoutedEventArgs>.
      /// </remarks>
      public void AppendEvent(EventInfo info, MemberMetadata eventData) {
         writer.Write($"public EventImplementation<{eventData.HandlerArgsType}> {info.Name} = new EventImplementation<{eventData.HandlerArgsType}>();");
         writer.Write(string.Empty);
         writer.Write($"event {eventData.HandlerType} {eventData.DeclaringType}.{info.Name}");
         using (writer.Indent()) {
            writer.Write("add");
            using (writer.Indent()) {
               writer.Write($"{info.Name}.add(new System.EventHandler<{eventData.HandlerArgsType}>(value));");
            }
            writer.Write("remove");
            using (writer.Indent()) {
               writer.Write($"{info.Name}.remove(new System.EventHandler<{eventData.HandlerArgsType}>(value));");
            }
         }
      }

      /// <remarks>
      /// Stub properties are similar to Stub events. In both cases, a special type has been created
      /// to help make a public field act like that sort of member.
      /// 
      /// PropertyImplementation provides .get, .set, and .value.
      /// It also provides implicit casting, allowing you to carelessly use the lazy syntax of treating the implementation exactly as the property.
      /// Example: stub.SomeIntProperty = 7;
      /// (as opposed to): stub.SomeIntProperty.value = 7;
      /// 
      /// The explicit implementation just forwards to the public field's get/set members.
      /// </remarks>
      public void AppendProperty(PropertyInfo info, MemberMetadata property) {
         // define the backing field
         writer.Write($"public PropertyImplementation<{property.ReturnType}> {property.Name} = new PropertyImplementation<{property.ReturnType}>();" + Environment.NewLine);

         // define the interface's property
         writer.Write($"{property.ReturnType} {property.DeclaringType}.{property.Name}");
         using (writer.Indent()) {
            if (info.CanRead) {
               writer.Write("get");
               using (writer.Indent()) {
                  writer.Write($"return this.{property.Name}.get();");
               }
            }
            if (info.CanWrite) {
               writer.Write("set");
               using (writer.Indent()) {
                  writer.Write($"this.{property.Name}.set(value);");
               }
            }
         }
      }

      /// <remarks>
      /// Since Item properties in .net have parameters, the Item property has to be handled specially.
      /// Instead of using a PropertyImplementation object, two separate delegates are exposed, named get_Item and set_Item.
      /// The get and set of the Item property forward to these two public fields.
      /// If no implementation is provided, get_Item will just return default.
      /// </remarks>
      public void AppendItemProperty(PropertyInfo info, MemberMetadata property) {
         if (info.CanRead) {
            writer.Write($"public System.Func<{property.ParameterTypes}, {property.ReturnType}> get_Item = ({property.ParameterNames}) => default({property.ReturnType});" + Environment.NewLine);
         }

         if (info.CanWrite) {
            writer.Write($"public System.Action<{property.ParameterTypes}, {property.ReturnType}> set_Item = ({property.ParameterNames}, value) => {{}};" + Environment.NewLine);
         }

         writer.Write($"{property.ReturnType} {property.DeclaringType}.this[{property.ParameterTypesAndNames}]");
         using (writer.Indent()) {
            if (info.CanRead) {
               writer.Write("get");
               using (writer.Indent()) {
                  writer.Write($"return get_Item({property.ParameterNames});");
               }
            }
            if (info.CanWrite) {
               writer.Write("set");
               using (writer.Indent()) {
                  writer.Write($"set_Item({property.ParameterNames}, value);");
               }
            }
         }
      }

      private string GetStubName(string returnType, string parameterTypes) {
         if (returnType == "void") {
            var delegateName = "System.Action";
            if (parameterTypes != string.Empty) delegateName += $"<{parameterTypes}>";
            return delegateName;
         } else {
            var delegateName = "System.Func";
            delegateName += parameterTypes == string.Empty ? $"<{returnType}>" : $"<{parameterTypes}, {returnType}>";
            return delegateName;
         }
      }

      private static string GetDefaultClause(string returnType) {
         return returnType == "void" ? string.Empty : $"default({returnType});";
      }
      
      /// <summary>
      /// When converting type lists into extensions to put on the end of method names,
      /// we have to sanitize them by removing characters that are illegal in C# member names.
      /// </summary>
      private static string SanitizeMethodName(string name) {
         return name
            .Replace(", ", "_")
            .Replace(">", "_")
            .Replace("<", "_")
            .Replace(".", "_");
      }

      private void ImplementInterfaceMethod(MethodInfo info, string localImplementationName, MemberMetadata method) {
         var call = $"this.{localImplementationName}";

         writer.Write($"{method.ReturnType} {method.DeclaringType}.{method.Name}({method.ParameterTypesAndNames})");
         using (writer.Indent()) {
            writer.AssignDefaultValuesToOutParameters(info.DeclaringType.Namespace, info.GetParameters());

            writer.Write($"if ({call} != null)");
            using (writer.Indent()) {
               var returnClause = method.ReturnType == "void" ? string.Empty : "return ";
               writer.Write($"{returnClause}{call}({method.ParameterNames});");
            }
            if (method.ReturnType != "void") {
               writer.Write("else");
               using (writer.Indent()) {
                  writer.Write($"return default({method.ReturnType});");
               }
            }
         }
      }
   }
}
