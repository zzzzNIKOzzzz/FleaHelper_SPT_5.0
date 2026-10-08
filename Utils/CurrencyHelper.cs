using UnityEngine;

namespace FleaHelper.Utils;

public static class CurrencyHelper
{
    public const string RoubleTpl = "5449016a4bdc2d86028b456f";
    public const string DollarTpl = "5696686a4bdc2d86028b456d";
    public const string EuroTpl = "569668774bdc2d86028b456d";

    public static int ConvertToUSD(float roublePrice) => Mathf.RoundToInt(roublePrice / 148f);
    public static int ConvertToEUR(float roublePrice) => Mathf.RoundToInt(roublePrice / 155f);
}
