using System.Globalization;
using System.Text.Json;

namespace PowerQuota.Core.Utilities;

public static class JsonHelper
{
    public static bool TryGetDoubleValue(this JsonElement element, out double value)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetDouble(out value);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var str = element.GetString();
            if (!string.IsNullOrWhiteSpace(str) &&
                double.TryParse(str, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    public static bool TryGetSingleValue(this JsonElement element, out float value)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetSingle(out value);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var str = element.GetString();
            if (!string.IsNullOrWhiteSpace(str) &&
                float.TryParse(str, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    public static bool TryGetInt64Value(this JsonElement element, out long value)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetInt64(out value);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var str = element.GetString();
            if (!string.IsNullOrWhiteSpace(str) &&
                long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    public static bool TryGetInt32Value(this JsonElement element, out int value)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetInt32(out value);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var str = element.GetString();
            if (!string.IsNullOrWhiteSpace(str) &&
                int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    public static bool TryGetPropertyDouble(this JsonElement element, string propertyName, out double value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var prop))
        {
            return prop.TryGetDoubleValue(out value);
        }
        value = 0;
        return false;
    }

    public static bool TryGetPropertySingle(this JsonElement element, string propertyName, out float value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var prop))
        {
            return prop.TryGetSingleValue(out value);
        }
        value = 0;
        return false;
    }

    public static bool TryGetPropertyInt64(this JsonElement element, string propertyName, out long value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var prop))
        {
            return prop.TryGetInt64Value(out value);
        }
        value = 0;
        return false;
    }

    public static bool TryGetPropertyInt32(this JsonElement element, string propertyName, out int value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var prop))
        {
            return prop.TryGetInt32Value(out value);
        }
        value = 0;
        return false;
    }
}
