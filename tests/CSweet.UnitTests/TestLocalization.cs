using System.Reflection;
using CSweet.UI.Localization;
using CSweet.UI.Services;

namespace CSweet.UnitTests;

/// <summary>Supplies English localization to components that tests construct directly instead of rendering.</summary>
internal static class TestLocalization
{
    private static readonly AppCulture Culture = new(null, new AuthSessionStore());
    public static readonly IAppLocalizer Localizer = new AppLocalizer(new LocalizationResources(), Culture);
    public static readonly UserTime Time = new(Culture);

    public static T Inject<T>(T component) where T : class
    {
        for (var type = component.GetType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!property.CanWrite) continue;
                if (property.PropertyType == typeof(IAppLocalizer)) property.SetValue(component, Localizer);
                else if (property.PropertyType == typeof(UserTime)) property.SetValue(component, Time);
            }
        }

        return component;
    }
}
