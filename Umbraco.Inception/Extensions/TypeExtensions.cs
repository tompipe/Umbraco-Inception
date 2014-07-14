using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Umbraco.Inception.Extensions
{
    public static class TypeExtensions
    {
        public static IEnumerable<PropertyInfo> GetPropertiesWithAttribute<TAttr>(this Type t, bool inherited = false) where TAttr : Attribute
        {
            BindingFlags flags = flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public;
            if (!inherited)
            {
                flags = flags | BindingFlags.DeclaredOnly;
            }

            return t.GetProperties(flags).Where(p => p.HasAttribute<TAttr>());
        }

        public static bool HasAttribute<TAttr>(this MemberInfo memberInfo) where TAttr : Attribute
        {
            return memberInfo.GetCustomAttribute<TAttr>() != null;
        }
    }
}
