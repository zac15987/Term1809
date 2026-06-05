using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace Term1809.Terminal.Internals {

    /// <summary>
    /// Factory that registers write-only WPF <see cref="DependencyProperty"/> entries for a
    /// <typeparamref name="CONTROL_TYPE"/> owner.  A write-only DP forwards every incoming value
    /// directly to a CLR property setter and never retains that value inside the dependency
    /// property system (the coerce callback always returns <see langword="null"/>).
    /// </summary>
    /// <remarks>
    /// This class is a static utility; it must never be instantiated.
    /// </remarks>
    /// <typeparam name="CONTROL_TYPE">
    /// The <see cref="UserControl"/> subclass that owns the dependency properties produced here.
    /// </typeparam>
    internal static class WriteOnlyDpFactory<CONTROL_TYPE> where CONTROL_TYPE : UserControl {

        /// <summary>
        /// Registers a new write-only <see cref="DependencyProperty"/> whose name and target
        /// CLR property are inferred from <paramref name="propToSet"/>.
        /// </summary>
        /// <typeparam name="PROP_TYPE">The value type carried by the dependency property.</typeparam>
        /// <param name="propToSet">
        /// A member-access lambda (e.g. <c>ctrl => ctrl.MyProperty</c>) that identifies the CLR
        /// property whose setter should be invoked whenever the DP value is pushed from XAML or
        /// a binding.
        /// </param>
        /// <returns>The newly registered <see cref="DependencyProperty"/>.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="propToSet"/> does not resolve to an accessible instance
        /// property on <typeparamref name="CONTROL_TYPE"/>.
        /// </exception>
        public static DependencyProperty GenerateWriteOnlyProperty<PROP_TYPE>(
            Expression<Func<CONTROL_TYPE, PROP_TYPE>> propToSet) {

            // Unwrap the lambda body to a member-access node so we can read the property name.
            if (propToSet.Body is not MemberExpression memberAccess) {
                throw new ArgumentException(
                    "Expression must be a direct property access (e.g. ctrl => ctrl.Foo).",
                    nameof(propToSet));
            }

            string targetName = memberAccess.Member.Name;

            // Resolve the actual PropertyInfo so we can obtain the setter MethodInfo once,
            // rather than re-resolving it on every coerce invocation.
            PropertyInfo resolvedProp = typeof(CONTROL_TYPE)
                .GetProperty(targetName, BindingFlags.Instance | BindingFlags.Public);

            if (resolvedProp is null) {
                throw new ArgumentException(
                    $"No public instance property '{targetName}' found on {typeof(CONTROL_TYPE).Name}.",
                    nameof(propToSet));
            }

            MethodInfo setter = resolvedProp.SetMethod
                ?? throw new ArgumentException(
                    $"Property '{targetName}' on {typeof(CONTROL_TYPE).Name} has no accessible setter.",
                    nameof(propToSet));

            // Build a coerce-value callback that:
            //   1. Calls the CLR setter with the incoming value (side-effect delivery).
            //   2. Returns null so the DP storage slot is always empty (write-only semantics).
            CoerceValueCallback forwardAndDiscard = (dependencyObj, incomingValue) => {
                setter.Invoke(dependencyObj, [incomingValue]);
                return null;
            };

            var metadata = new FrameworkPropertyMetadata(
                defaultValue: null,
                propertyChangedCallback: null,
                coerceValueCallback: forwardAndDiscard);

            return DependencyProperty.Register(
                targetName,
                typeof(PROP_TYPE),
                typeof(CONTROL_TYPE),
                metadata);
        }
    }

}
