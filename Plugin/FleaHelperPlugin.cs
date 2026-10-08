#nullable disable
using System;
using System.Reflection;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Comfort.Common;
using EFT.Communications;
using FleaHelper.Patches;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

// ============================================================================
//  FleaHelper — плагин быстрой продажи предметов на барахолке (бар) для SPT
//  
//  Функциональность:
//    • Быстрая продажа выбранного предмета на барахолку одной кнопкой
//    • Переключение валюты продажи (рубли / доллары / евро)
//    • Автоматическое определение цены из справочника (Handbook)
//    • Показ внутриигрового уведомления о результате
//  
//  Технические особенности:
//    • Вся работа с игровыми типами — через рефлексию (IL2CPP-интероп)
//    • Поддержка MongoID — 24-символьные hex-идентификаторы EFT
//    • Совместимость с Il2CppReferenceArray<T> и Il2CppStringArray
//    • Делегаты конвертируются через DelegateSupport.ConvertDelegate
// ============================================================================

namespace FleaHelper
{
// Главный класс плагина. Регистрируется в BepInEx как модуль.
// Управляет конфигурацией, Harmony-патчами и компонентом ввода.

[BepInPlugin("com.zzzNIKOzzz.fleahelper", "FleaHelper", PluginVersion)]
internal class FleaHelperPlugin : BasePlugin
{
    public const string PluginVersion = "1.0.0";
    internal static new ManualLogSource Log;
    // === Настройки конфигурации ===
    // Клавиша, при удержании которой происходит быстрая продажа.
    public static ConfigEntry<KeyCode> Hotkey { get; set; }
    // Показывать цену лота в всплывающей подсказке.
    public static ConfigEntry<bool> ShowListingPrice { get; set; }
    // Игнорировать лимиты выставления лотов на барахолке.
    public static ConfigEntry<bool> BypassLimit { get; set; }
    // Отключить ванильный мигающий эффект при ожидании ответа сервера.
    public static ConfigEntry<bool> SkipMode { get; set; }
    // Валюта выставления лота (рубли, доллары, евро).
    public static ConfigEntry<EPostingCurrency> PostingCurrency { get; set; }
    // Клавиша для быстрого переключения валюты продажи.
    public static ConfigEntry<KeyCode> ChangeCurrencyKey { get; set; }


    // Массив всех значений enum EPostingCurrency — используется для
    // циклического переключения валюты по кругу.
    private static readonly EPostingCurrency[] _currencyValues =
        (EPostingCurrency[])Enum.GetValues(typeof(EPostingCurrency));

    // Template ID валют для барахолки.
    // Ключ — enum валюты, значение — 24-символьный MongoID предмета-валюты.
    private static readonly Dictionary<EPostingCurrency, string> CurrencyIds = new()
    {
        { EPostingCurrency.RUB, "5449016a4bdc2d6f028b456f" },  // Рубли
        { EPostingCurrency.USD, "5696686a4bdc2d6f028b456a" },  // Доллары
        { EPostingCurrency.EUR, "569668774bdc2d6f028b4568" }   // Евро
    };

    // Кэш локализации, загруженный из JSON-файла сервера.
    // SPT 5.0 хранит локали в SPT_Data/database/locales/global/ru.json.
    private static Dictionary<string, string> _localeCache = null;

    // Точка входа плагина. Вызывается BepInEx при загрузке.
    // Регистрирует конфигурацию, применяет Harmony-патчи и добавляет
    // компонент обработки ввода (FleaHelperBehaviour) в сцену.
    public override void Load()
    {
        Log = base.Log;
		Log.LogInfo("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz");
        Log.LogInfo($"{nameof(FleaHelperPlugin)} v{PluginVersion} by zzzNIKOzzz загружен.");
		Log.LogInfo("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz");
		Log.LogInfo("zzzNIKOzzz: Создать лот? Слишком долго! OneButtonSell сучечка!!!!!!!");

        // === Регистрация настроек в конфигурационном файле BepInEx ===
        Hotkey = Config.Bind("FleaHelper", "Быстрая клавиша", KeyCode.LeftControl,
            "Клавиша, которая отвечает за быструю продажу");
        ShowListingPrice = Config.Bind("FleaHelper", "Показать цену лота", false,
            "Показывать цену лота в всплывающей подсказке");
        BypassLimit = Config.Bind("FleaHelper", "Лимит продажи", false,
            "Игнорировать лимиты выставления лотов на барахолке");
        SkipMode = Config.Bind("FleaHelper", "Skip Mode", true,
            "Отключить ванильный мигающий эффект при ожидании ответа сервера");
        PostingCurrency = Config.Bind("FleaHelper", "Валюта продажи", EPostingCurrency.RUB,
            "Валюта выставления лота рубли, доллары, евро");
        ChangeCurrencyKey = Config.Bind("FleaHelper", "Бытрое изменение валюты", KeyCode.None,
            "Клавиша для быстрого переключения валюты продажи");

        // Применяем все Harmony-патчи, объявленные в текущей сборке
        var harmony = new Harmony("com.fleahelper");
        harmony.PatchAll();
        // Добавляем MonoBehaviour-компонент для отслеживания нажатий клавиш
        AddComponent<FleaHelperBehaviour>();
    }
    // Циклическое переключение валюты продажи (RUB → USD → EUR → RUB).
    // Вызывается при нажатии клавиши ChangeCurrencyKey.
    internal static void ChangeCurrency()
    {
        var currentIndex = Array.IndexOf(_currencyValues, PostingCurrency.Value);
        PostingCurrency.Value = _currencyValues[(currentIndex + 1) % _currencyValues.Length];
        ShowNotification("Валюта установлена на" + PostingCurrency.Value);
    }
    // ============================================================================
    //  ОСНОВНОЙ МЕТОД — БЫСТРАЯ ПРОДАЖА ПРЕДМЕТА НА БАРАХОЛКЕ
    //  
    //  Алгоритм:
    //    1. Получить ItemUiContext (контекст UI предметов)
    //    2. Получить CurrentItemContext (контекст выбранного предмета)
    //    3. Извлечь предмет, TemplateId, ItemId и ShortName
    //    4. Получить цену из справочника (Handbook)
    //    5. Получить экземпляр RagFair (барахолки)
    //    6. Создать оффер через AddOffer
    //    7. Показать уведомление о результате
    // ============================================================================
	
    // Выставляет выбранный предмет на барахолку.
    // Работает полностью через рефлексию, так как игровые типы
    // находятся в IL2CPP-сборках и недоступны напрямую.
    internal static void PostItemToFlea()
    {
        try
        {
            // === ШАГ 1. Получение ItemUiContext ===
            // ItemUiContext — это главный UI-контекст предметов в EFT.
            // Через него доступны выбранный предмет, справочник цен и барахолка.
            Type itemUiContextType = FindType("EFT.UI.ItemUiContext");
            if (itemUiContextType == null)
            {
                ShowNotification("ItemUiContext не найдено");
                return;
            }
            object itemUiContext = null;

            // Способ 1: статическое свойство Instance
            var staticInstanceProp = itemUiContextType.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            if (staticInstanceProp != null)
            {
                itemUiContext = staticInstanceProp.GetValue(null);
            }

            // Способ 2: Singleton<T>.Instance
            if (itemUiContext == null)
                itemUiContext = GetSingletonInstance(itemUiContextType);
            
            // Способ 3: FindObjectOfType (поиск по сцене)
            if (itemUiContext == null)
            {
                try
                {
                    var findMethod = typeof(UnityEngine.Object).GetMethod("FindObjectOfType",
                        BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Type) }, null);
                    if (findMethod != null)
                        itemUiContext = findMethod.Invoke(null, new object[] { itemUiContextType });
                }
                catch { }
            }

            if (itemUiContext == null)
            {
                ShowNotification("Нет контекста предмета (нет в инвентаре?)");
                return;
            }
            // === ШАГ 2. Получение CurrentItemContext ===
            // CurrentItemContext — контекст предмета, который сейчас выбран
            // (наведён курсором или открыт в контекстном меню).
            object itemContext = GetPropertyValue(itemUiContext, itemUiContextType, "CurrentItemContext")
                                 ?? GetPropertyValue(itemUiContext, itemUiContextType, "_CurrentItemContext_k__BackingField");

            if (itemContext == null)
            {
                ShowNotification("Предмет не выбран");
                return;
            }
            // === ШАГ 3. Извлечение предмета и его идентификаторов ===
            var itemContextType = itemContext.GetType();
            object item = GetPropertyValue(itemContext, itemContextType, "Item")
                          ?? GetFieldValue(itemContext, itemContextType, "Item");

            if (item == null)
            {
                ShowNotification("Нет предмета для продажи");
                return;
            }

            var itemType = item.GetType();

            // TemplateId — идентификатор шаблона предмета (MongoID, 24 hex-символа).
            // Используется для поиска цены в справочнике.
            object templateIdObj = GetPropertyValue(item, itemType, "TemplateId");
            string templateId = ExtractMongoId(templateIdObj) ?? "unknown";

            // ItemId — уникальный идентификатор конкретного экземпляра предмета
            // (не путать с TemplateId — это ID шаблона, а не конкретного предмета).
            string itemId = ExtractItemId(item, itemType);

            // ShortName — короткое название предмета для отображения в уведомлении.
            // Читается из Template.ShortName, так как item.ShortName может вернуть MongoID.
            string shortName = GetShortName(item, itemType, templateId);
            Log.LogInfo("Имя предмета: " + shortName);

            // === ШАГ 4. Получение цены из справочника (Handbook) ===
            // Handbook — внутриигровой справочник, хранящий базовые цены предметов.
            // Последовательно перебираем несколько методов получения цены,
            // так как API справочника может отличаться между версиями SPT.
            object handbook = GetPropertyValue(itemUiContext, itemUiContextType, "Handbook");
            int price = 0;

            if (handbook != null)
            {
                var handbookType = handbook.GetType();

                // Попытка 1: GetBasePrice(MongoID) — базовая цена предмета
                if (templateIdObj != null)
                {
                    var basePriceMethod = handbookType.GetMethod("GetBasePrice", BindingFlags.Public | BindingFlags.Instance);
                    if (basePriceMethod != null)
                    {
                        try
                        {
                            var result = basePriceMethod.Invoke(handbook, new object[] { templateIdObj });
                            if (result != null)
                            {
                                price = Convert.ToInt32(result);
                            }
                        }
                        catch { }
                    }
                }

                // Попытка 2: перебор альтернативных методов цен
                // Разные версии SPT могут использовать разные названия методов.
                if (price <= 0)
                {
                    string[] priceMethodNames = { "GetFleaPriceForItem", "GetItemPrice", "GetFleaPrice",
                                                  "GetItemValue", "GetHandbookPrice", "GetAvgFleaPrice",
                                                  "GetItemMinAvgMaxFleaPriceValues" };
                    foreach (var methodName in priceMethodNames)
                    {
                        if (price > 0) break;
                        var priceMethod = handbookType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
                        if (priceMethod == null) continue;

                        var parms = priceMethod.GetParameters();

                        try
                        {
                            // Подбираем аргументы по типам параметров
                            object[] methodArgs = new object[parms.Length];
                            for (int i = 0; i < parms.Length; i++)
                            {
                                var pt = parms[i].ParameterType;
                                if (pt == typeof(string))
                                    methodArgs[i] = templateId;
                                else if (templateIdObj != null && pt.IsInstanceOfType(templateIdObj))
                                    methodArgs[i] = templateIdObj;
                                else if (pt == typeof(int) || pt == typeof(long))
                                    methodArgs[i] = 0;
                                else
                                    methodArgs[i] = null;
                            }

                            var result = priceMethod.Invoke(handbook, methodArgs);
                            if (result != null)
                            {
                                price = Convert.ToInt32(result);
                            }
                        }
                        catch { }
                    }
                }
                // Fallback внутри Handbook: если ни один метод не сработал — ставим 10000
                if (price <= 0)
                {
                    price = 10000;
                }
            }
            // Попытка 3: CreditsPrice из шаблона предмета
            // Если Handbook недоступен, читаем цену напрямую из Template.CreditsPrice.
            if (price <= 0)
            {
                object template = GetPropertyValue(item, itemType, "Template");
                if (template != null)
                {
                    var templateType = template.GetType();
                    object creditsPriceObj = GetPropertyValue(template, templateType, "CreditsPrice")
                                             ?? GetFieldValue(template, templateType, "CreditsPrice");
                    if (creditsPriceObj != null)
                    {
                        price = Convert.ToInt32(creditsPriceObj);
                    }
                }
            }
            // Финальный fallback: если цена так и не получена — ставим 10000
            if (price <= 0)
            {
                price = 10000;
            }
            // === ШАГ 5. Получение экземпляра RagFair (барахолки) ===
            // RagFair — это класс, отвечающий за интерфейс барахолки.
            // Сначала ищем его через ItemUiContext._ragfair,
            // затем через Singleton.
            object ragfairInstance = GetPropertyValue(itemUiContext, itemUiContextType, "_ragfair")
                                     ?? GetPropertyValue(itemUiContext, itemUiContextType, "Ragfair");

            if (ragfairInstance == null)
            {
                Type ragfairType = FindType("EFT.UI.Ragfair.RagFair");
                if (ragfairType != null)
                    ragfairInstance = GetSingletonInstance(ragfairType);
            }

            if (ragfairInstance == null)
            {
                ShowNotification("Барахолка недоступна");
                return;
            }

            var ragType = ragfairInstance.GetType();

            // === ШАГ 6. Создание оффера (выставление на барахолку) ===
            // Получаем Template ID выбранной валюты и вызываем CreateOffer.
            string currencyId = CurrencyIds.GetValueOrDefault(PostingCurrency.Value, CurrencyIds[EPostingCurrency.RUB]);

            bool success = CreateOffer(ragfairInstance, ragType, itemId, currencyId, price, shortName);

            // === ШАГ 7. Уведомление о результате ===
            if (success)
            {
                ShowNotification($"Лот создан: {shortName} по цене {price}");
            }
            else
            {
                ShowNotification($"Невозможно выставить лот {shortName} - смотри лог");
            }
        }
        catch (Exception ex)
        {
            Log.LogError("PostItemToFlea failed: " + ex.Message + "\n" + ex.StackTrace);
            ShowNotification("Ошибка: " + ex.Message);
        }
    }

    // ============================================================================
    //  СОЗДАНИЕ ОФФЕРА НА БАРАХОЛКЕ
    // ============================================================================

    // Создаёт оффер через метод AddOffer у RagFair.
    // Подбирает аргументы для каждого параметра метода по типу:
    //   • bool      → false (не обходить лимиты)
    //   • string    → itemId предмета
    //   • string[]  → массив с одним itemId (Il2CppStringArray)
    //   • array     → массив требований (требуемая валюта + цена)
    //   • Action    → callback-делегат через DelegateSupport
    // 	 • true      → если оффер успешно создан.
    private static bool CreateOffer(object ragfair, Type ragType, string itemId, string currencyId, int price, string shortName)
    {
        try
        {
            // Ищем метод AddOffer или AddPlayerOffer
            var addOfferMethod = ragType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "AddOffer" || m.Name == "AddPlayerOffer");

            if (addOfferMethod == null)
            {
                return false;
            }

            var parameters = addOfferMethod.GetParameters();

            // Подбираем аргументы для каждого параметра по типу
            object[] args = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var paramType = parameters[i].ParameterType;

                if (paramType == typeof(bool))
                {
                    // bool-параметр — скорее всего флаг обхода лимитов
                    args[i] = false;
                }
                else if (paramType == typeof(string))
                {
                    // string-параметр — ID предмета
                    args[i] = itemId;
                }
                else if (paramType.Name == "Il2CppStringArray" || paramType.Name == "String[]")
                {
                    // Массив строк с ID предметов — создаём Il2Cpp-совместимый массив
                    args[i] = CreateIl2CppStringArray(itemId, paramType);
                }
                else if (paramType.IsArray && paramType.Name.Contains("Il2Cpp"))
                {
                    // IL2CPP-массив требований (валюта + цена)
                    args[i] = CreateRequirementsArray(paramType, currencyId, price);
                }
                else if (paramType.Name.Contains("Il2CppReferenceArray"))
                {
                    // Il2CppReferenceArray<T> — это класс, а не массив.
                    // paramType.IsArray для него вернёт false, поэтому
                    // нужна отдельная проверка через Name.Contains.
                    args[i] = CreateRequirementsArray(paramType, currencyId, price);
                }
                else if (paramType.Name == "Action" || paramType.Name.Contains("Action"))
                {
                    // Action-делегат — callback после создания оффера
                    args[i] = CreateCallback(paramType);
                }
                else
                {
                    // Неизвестный тип — для значимых типов создаём default,
                    // для ссылочных — null
                    try
                    {
                        args[i] = paramType.IsValueType ? Activator.CreateInstance(paramType) : null;
                    }
                    catch
                    {
                        args[i] = null;
                    }
                }
            }

            // Вызываем AddOffer с подготовленными аргументами
            addOfferMethod.Invoke(ragfair, args);
            return true;
        }
        catch (Exception ex)
        {
            Log.LogError("Ошибка создания предложения: " + ex.Message + "\n" + ex.StackTrace);
            return false;
        }
    }
    // Создаёт IL2CPP-совместимый массив строк с ID предмета.
    // Пробует три способа создания:
    //   1. Конструктор string[] (нативный массив C# → Il2Cpp)
    //   2. Конструктор int (длина) + индексатор
    //   3. Il2CppSystem.Array.CreateInstance
    private static object CreateIl2CppStringArray(string itemId, Type arrayType)
    {
        // Способ 1: конструктор, принимающий string[]
        var ctor = arrayType.GetConstructor(new[] { typeof(string[]) });
        if (ctor != null)
        {
            return ctor.Invoke(new object[] { new[] { itemId } });
        }

        // Способ 2: конструктор, принимающий длину массива
        ctor = arrayType.GetConstructor(new[] { typeof(int) });
        if (ctor != null)
        {
            var arr = ctor.Invoke(new object[] { 1 });
            var indexer = arrayType.GetProperty("Item");
            if (indexer != null)
            {
                indexer.SetValue(arr, itemId, new object[] { 0 });
            }
            return arr;
        }

        // Способ 3: Il2CppSystem.Array.CreateInstance
        // Создаём native-массив через IL2CPP и заполняем его.
        try
        {
            var elementType = arrayType.GetElementType();
            if (elementType != null)
            {
                var il2cppType = Il2CppType.From(elementType);
                var arr = Il2CppSystem.Array.CreateInstance(il2cppType, 1);
                // Каст строки: string → Il2CppSystem.String → Il2CppSystem.Object
                arr.SetValue((Il2CppSystem.Object)(Il2CppSystem.String)itemId, 0);
                return arr;
            }
        }
        catch { }

        return null;
    }
    // Создаёт массив требований для оффера.
    // Требование — это объект с полями _tpl (ID валюты) и count (цена).
    // Для Il2CppReferenceArray<T> метод GetElementType() возвращает null,
    // поэтому используется GetGenericArguments() для получения типа элемента.
    // Пробует три способа создания массива:
    //   1. Конструктор(int) + индексатор
    //   2. Конструктор(T[]) — нативный C#-массив
    //   3. Il2CppSystem.Array.CreateInstance
    private static object CreateRequirementsArray(Type arrayType, string currencyId, int price)
    {
        try
        {
            // Определяем тип элемента массива.
            // Для Il2CppReferenceArray<T> GetElementType() возвращает null,
            // поэтому используем GetGenericArguments().
            Type elementType = arrayType.GetElementType();
            if (elementType == null && arrayType.IsGenericType)
                elementType = arrayType.GetGenericArguments().FirstOrDefault();

            if (elementType == null)
            {
                return null;
            }

            // Создаём экземпляр требования (Requirement) через Activator
            object requirement = null;
            try
            {
                requirement = Activator.CreateInstance(elementType);
            }
            catch
            {
                return null;
            }

            if (requirement == null)
            {
                return null;
            }
            // Заполняем поля требования:
            //   _tpl  — Template ID валюты (например, рубли: 5449016a4bdc2d6f028b456f)
            //   count — цена лота
            SetFieldOrProp(requirement, elementType, "_tpl", currencyId);
            SetFieldOrProp(requirement, elementType, "count", (double)price);

            // Способ 1: конструктор(int) + индексатор
            try
            {
                var ctor = arrayType.GetConstructor(new[] { typeof(int) });
                if (ctor != null)
                {
                    var arr = ctor.Invoke(new object[] { 1 });
                    var indexer = arrayType.GetProperty("Item");
                    if (indexer != null)
                    {
                        indexer.SetValue(arr, (Il2CppSystem.Object)requirement, new object[] { 0 });
                        return arr;
                    }
                }
            }
            catch { }

            // Способ 2: конструктор(T[]) — передаём нативный C#-массив
            try
            {
                var arrCtor = arrayType.GetConstructor(new[] { elementType.MakeArrayType() });
                if (arrCtor != null)
                {
                    var arr = arrCtor.Invoke(new object[] { new[] { requirement } });
                    return arr;
                }
            }
            catch { }

            // Способ 3: Il2CppSystem.Array.CreateInstance
            try
            {
                var il2cppElementType = Il2CppType.From(elementType);
                var arr = Il2CppSystem.Array.CreateInstance(il2cppElementType, 1);
                arr.SetValue((Il2CppSystem.Object)requirement, 0);
                return arr;
            }
            catch { }

            return null;
        }
        catch (Exception ex)
        {
            Log.LogError("Ошибка создания масива требований: " + ex.Message + "\n" + ex.StackTrace);
            return null;
        }
    }
    // Создаёт IL2CPP-совместимый callback-делегат.
    // Il2CppSystem.Action не принимает параметров (в отличие от System.Action<object>).
    // Делегат конвертируется через DelegateSupport.ConvertDelegate<T>,
    // который создаёт мост между Mono-делегатом и IL2CPP-делегатом.
    private static object CreateCallback(Type callbackType)
    {
        // Создаём простой callback без параметров — логируем факт вызова.
        // Il2CppSystem.Action имеет 0 параметров.
        Action callback = () =>
        {
        };
        // Конвертируем C#-делегат в IL2CPP-делегат через DelegateSupport
        try
        {
            var convertMethod = typeof(DelegateSupport).GetMethod("ConvertDelegate", BindingFlags.Public | BindingFlags.Static);
            if (convertMethod != null)
            {
                var genericConvert = convertMethod.MakeGenericMethod(callbackType);
                return genericConvert.Invoke(null, new object[] { callback });
            }
        }
        catch { }

        return null;
    }
    // ============================================================================
    //  ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ
    //  Все методы ниже работают через рефлексию и обеспечивают
    //  совместимость с IL2CPP-типами EFT.
    // ============================================================================
    // Извлекает 24-символьный hex-идентификатор из объекта MongoID.
    // MongoID в EFT — это структура, хранящая ID предмета в виде
    // 24-символьной hex-строки (например, "5449016a4bdc2d6f028b456f").
    // Сначала пробует ToString(), затем перебирает все свойства и поля
    // в поисках строки, подходящей под формат MongoID.
    // 24-символьная hex-строка или null.
    private static string ExtractMongoId(object mongoIdObj)
    {
        if (mongoIdObj == null) return null;

        // Попытка 1: ToString() сразу даёт hex-строку
        string idStr = mongoIdObj.ToString();
        if (idStr != null && idStr.Length == 24 && IsHexString(idStr))
            return idStr;

        // Попытка 2: перебор всех свойств
        var type = mongoIdObj.GetType();
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
        {
            try
            {
                var val = prop.GetValue(mongoIdObj);
                if (val != null)
                {
                    string strVal = val.ToString();
                    if (strVal.Length == 24 && IsHexString(strVal))
                    {
                        return strVal;
                    }
                }
            }
            catch { }
        }

        // Попытка 3: перебор всех полей
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
        {
            try
            {
                var val = field.GetValue(mongoIdObj);
                if (val != null)
                {
                    string strVal = val.ToString();
                    if (strVal.Length == 24 && IsHexString(strVal))
                    {
                        return strVal;
                    }
                }
            }
            catch { }
        }

        return idStr;
    }

    // Проверяет, состоит ли строка только из hex-символов (0-9, a-f, A-F).
    private static bool IsHexString(string str)
    {
        if (string.IsNullOrEmpty(str)) return false;
        foreach (char c in str)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        }
        return true;
    }
    // Извлекает уникальный идентификатор экземпляра предмета (ItemId).
    // Перебирает возможные имена свойств: "Id", "ItemId", "_id".
    // В отличие от TemplateId, ItemId идентифицирует конкретный
    // экземпляр предмета в инвентаре игрока.
    // ItemId или "unknown".
    private static string ExtractItemId(object item, Type itemType)
    {
        foreach (var propName in new[] { "Id", "ItemId", "_id" })
        {
            var val = GetPropertyValue(item, itemType, propName) ?? GetFieldValue(item, itemType, propName);
            if (val != null)
            {
                string idStr = ExtractMongoId(val) ?? val.ToString();
                if (!string.IsNullOrEmpty(idStr) && idStr != itemType.Name)
                    return idStr;
            }
        }

        return "unknown";
    }

    // Получает короткое название предмета для отображения в уведомлении.
    // Сначала пробует Template.ShortName — это надёжнее, так как
    // item.ShortName может вернуть MongoID вместо локализованной строки.
    // Если Template недоступен — fallback на item.ShortName.
    // Если оба не сработали — возвращает переданный fallback (templateId).
    // Получает короткое название предмета для отображения в уведомлении.
    // Сначала пробует чтение из JSON-файла локализации сервера (ru.json),
    // затем Template.ShortName через Localized(), затем item.ShortName.
    private static string GetShortName(object item, Type itemType, string fallback)
    {
        // Попытка 0: чтение из JSON-файла локализации (самый надёжный путь)
        string templateId = ExtractMongoId(GetPropertyValue(item, itemType, "TemplateId"));
        if (!string.IsNullOrEmpty(templateId) && templateId != "unknown")
        {
            string localeName = ResolveItemNameFromLocaleFile(templateId);
            if (!string.IsNullOrEmpty(localeName))
                return localeName;
        }

        // Попытка 1: Template.ShortName
        object template = GetPropertyValue(item, itemType, "Template");
        if (template != null)
        {
            var templateType = template.GetType();
            object templateShortName = GetPropertyValue(template, templateType, "ShortName");
            if (templateShortName != null)
            {
                string result = TryGetLocalizedString(templateShortName, templateId);
                if (!string.IsNullOrEmpty(result) && result != templateType.Name)
                    return result;
            }
        }

        // Попытка 2: item.ShortName напрямую
        object shortNameObj = GetPropertyValue(item, itemType, "ShortName");
        if (shortNameObj == null) return fallback;

        string sn = TryGetLocalizedString(shortNameObj, templateId);
        if (!string.IsNullOrEmpty(sn) && sn != itemType.Name)
            return sn;

        return fallback;
    }

    // Ищет название предмета в JSON-файле локализации сервера.
    // SPT 5.0 хранит локали в SPT_Data/database/locales/global/ru.json.
    // Формат может быть плоским ("templateId ShortName") или вложенным
    // ({"templates": {"templateId": {"ShortName": "..."}}}).
    private static string ResolveItemNameFromLocaleFile(string templateId)
    {
        if (string.IsNullOrEmpty(templateId)) return null;

        if (_localeCache == null)
        {
            _localeCache = LoadLocaleFile();
            if (_localeCache == null)
            {
                Log.LogWarning("[Локализация] Файл локализации не найден");
                return null;
            }
            Log.LogInfo($"[Локализация] Загружено {_localeCache.Count} записей локализации");

            // Дамп нескольких ключей для отладки
            int dumpCount = 0;
            foreach (var kvp in _localeCache)
            {
                if (kvp.Key.Contains("ShortName"))
                {
                    Log.LogInfo($"[Локализация] Sample key: '{kvp.Key}' -> '{kvp.Value}'");
                    dumpCount++;
                    if (dumpCount >= 5) break;
                }
            }
        }

        // Пробуем разные форматы ключей
        string[] keys = {
            templateId + " ShortName",
            "templates " + templateId + " ShortName",
            templateId + " Name",
            "templates " + templateId + " Name",
        };

        foreach (var key in keys)
        {
            if (_localeCache.TryGetValue(key, out string value) && !string.IsNullOrEmpty(value))
            {
                Log.LogInfo($"[Локализация] Найдено: '{key}' -> '{value}'");
                return value;
            }
        }

        // Fallback: ищем любой ключ, заканчивающийся на "templateId ShortName"
        foreach (var kvp in _localeCache)
        {
            if (kvp.Key.EndsWith(templateId + " ShortName") && !string.IsNullOrEmpty(kvp.Value))
            {
                Log.LogInfo($"[Локализация] Найдено (суффикс): '{kvp.Key}' -> '{kvp.Value}'");
                return kvp.Value;
            }
        }

        Log.LogInfo($"[Локализация] Не найдено имя для templateId: {templateId}");
        return null;
    }

    // Загружает JSON-файл локализации с диска.
    private static Dictionary<string, string> LoadLocaleFile()
    {
        try
        {
            string gameRoot = AppDomain.CurrentDomain.BaseDirectory;
            Log.LogInfo("[Локализация] Game root: " + gameRoot);

            string[] baseDirs = {
                Path.Combine(gameRoot, "SPT_Runtime", "SPT_Data", "database", "locales", "global"),
            };

            string[] languages = { "ru", "en" };

            foreach (var lang in languages)
            {
                foreach (var dir in baseDirs)
                {
                    string filePath = Path.Combine(dir, lang + ".json");
                    if (File.Exists(filePath))
                    {
                        Log.LogInfo("[Локализация] Найден файл: " + filePath);
                        string json = File.ReadAllText(filePath);
                        return ParseLocaleJson(json);
                    }
                }
            }

            // Fallback: рекурсивный поиск ru.json
            try
            {
                string[] files = Directory.GetFiles(gameRoot, "ru.json", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    if (file.Contains("locales") && file.Contains("global"))
                    {
                        Log.LogInfo("[Локализация] Найден файл (поиск): " + file);
                        string json = File.ReadAllText(file);
                        return ParseLocaleJson(json);
                    }
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            Log.LogWarning("[Локализация] Ошибка загрузки: " + ex.Message);
        }

        return null;
    }

    // Парсит JSON-файл локализации в Dictionary<string, string>.
    // Поддерживает оба формата: плоский и вложенный.
    private static Dictionary<string, string> ParseLocaleJson(string json)
    {
        var dict = new Dictionary<string, string>();
        try
        {
            int pos = 0;
            ParseJsonObject(json, ref pos, dict, "");
        }
        catch (Exception ex)
        {
            Log.LogWarning("[Локализация] Ошибка парсинга JSON: " + ex.Message);
        }
        return dict.Count > 0 ? dict : null;
    }

    // Рекурсивно парсит JSON-объект, собирая составные ключи.
    private static void ParseJsonObject(string json, ref int pos, Dictionary<string, string> dict, string prefix)
    {
        SkipJsonWhitespace(json, ref pos);
        if (pos >= json.Length || json[pos] != '{') return;
        pos++; // skip {

        while (true)
        {
            SkipJsonWhitespace(json, ref pos);
            if (pos >= json.Length) return;
            if (json[pos] == '}') { pos++; return; }

            // Parse key
            string key = ParseJsonString(json, ref pos);
            if (key == null) return;

            SkipJsonWhitespace(json, ref pos);
            if (pos >= json.Length || json[pos] != ':') return;
            pos++; // skip :
            SkipJsonWhitespace(json, ref pos);

            string fullKey = string.IsNullOrEmpty(prefix) ? key : prefix + " " + key;

            if (pos < json.Length && json[pos] == '"')
            {
                // String value
                string value = ParseJsonString(json, ref pos);
                if (value != null)
                    dict[fullKey] = value;
            }
            else if (pos < json.Length && json[pos] == '{')
            {
                // Nested object
                ParseJsonObject(json, ref pos, dict, fullKey);
            }
            else
            {
                // Array, number, bool, null — skip
                SkipJsonValue(json, ref pos);
            }

            SkipJsonWhitespace(json, ref pos);
            if (pos < json.Length && json[pos] == ',') pos++;
        }
    }

    // Парсит JSON-строку (текст в кавычках).
    private static string ParseJsonString(string json, ref int pos)
    {
        if (pos >= json.Length || json[pos] != '"') return null;
        pos++; // skip opening quote
        var sb = new System.Text.StringBuilder();

        while (pos < json.Length)
        {
            char c = json[pos];
            if (c == '"')
            {
                pos++; // skip closing quote
                return sb.ToString();
            }
            if (c == '\\' && pos + 1 < json.Length)
            {
                char next = json[pos + 1];
                if (next == '"') sb.Append('"');
                else if (next == '\\') sb.Append('\\');
                else if (next == 'n') sb.Append('\n');
                else if (next == 't') sb.Append('\t');
                else if (next == 'r') sb.Append('\r');
                else if (next == '/') sb.Append('/');
                else if (next == 'u' && pos + 5 < json.Length)
                {
                    string hex = json.Substring(pos + 2, 4);
                    try
                    {
                        int code = Convert.ToInt32(hex, 16);
                        sb.Append((char)code);
                        pos += 4;
                    }
                    catch
                    {
                        sb.Append(next);
                    }
                }
                else
                    sb.Append(next);
                pos += 2;
            }
            else
            {
                sb.Append(c);
                pos++;
            }
        }
        return sb.ToString();
    }

    // Пропускает пробелы в JSON.
    private static void SkipJsonWhitespace(string json, ref int pos)
    {
        while (pos < json.Length)
        {
            char c = json[pos];
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                pos++;
            else
                break;
        }
    }

    // Пропускает значение JSON (массив, объект, число, булев).
    private static void SkipJsonValue(string json, ref int pos)
    {
        if (pos >= json.Length) return;
        char c = json[pos];
        if (c == '[')
        {
            int depth = 0;
            while (pos < json.Length)
            {
                if (json[pos] == '[') depth++;
                else if (json[pos] == ']') { depth--; if (depth == 0) { pos++; return; } }
                pos++;
            }
        }
        else if (c == '{')
        {
            int depth = 0;
            while (pos < json.Length)
            {
                if (json[pos] == '{') depth++;
                else if (json[pos] == '}') { depth--; if (depth == 0) { pos++; return; } }
                pos++;
            }
        }
        else
        {
            while (pos < json.Length)
            {
                char ch = json[pos];
                if (ch == ',' || ch == '}' || ch == ']') return;
                pos++;
            }
        }
    }
    // Пытается извлечь локализованную строку из объекта LocalizedString.
    // Если объект — обычная C#-строка (ключ локализации), пытается
    // разрешить его через JSON-файл локализации сервера.
    private static string TryGetLocalizedString(object obj, string templateId = null)
    {
        if (obj == null) return null;

        // Если obj — обычная C#-строка, это ключ локализации.
        if (obj is string strKey)
        {
            if (!string.IsNullOrEmpty(templateId))
            {
                string resolved = ResolveItemNameFromLocaleFile(templateId);
                if (!string.IsNullOrEmpty(resolved))
                    return resolved;
            }
            return strKey;
        }

        var type = obj.GetType();

        // Получаем сырой ключ (_stringID)
        string rawKey = null;
        try
        {
            var field = type.GetField("_stringID", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null)
            {
                var val = field.GetValue(obj);
                if (val != null) rawKey = val.ToString();
            }
        }
        catch { }

        // Попытка 1: все перегрузки Localized()
        try
        {
            var localizedMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                .Where(m => m.Name == "Localized")
                .ToList();

            foreach (var method in localizedMethods)
            {
                try
                {
                    var parms = method.GetParameters();
                    object result;
                    if (parms.Length == 0)
                        result = method.Invoke(obj, null);
                    else
                    {
                        object[] args = new object[parms.Length];
                        result = method.Invoke(obj, args);
                    }
                    if (result != null)
                    {
                        string str = result.ToString();
                        if (!string.IsNullOrEmpty(str)
                            && !str.Contains("LocalizedString")
                            && !str.Contains("MongoID"))
                            return str;
                    }
                }
                catch { }
            }
        }
        catch { }

        // Попытка 2: свойство Value (с NonPublic)
        try
        {
            var valProp = type.GetProperty("Value",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (valProp != null)
            {
                var val = valProp.GetValue(obj);
                if (val != null)
                {
                    string str = val.ToString();
                    if (!string.IsNullOrEmpty(str)
                        && !str.Contains("LocalizedString")
                        && !str.Contains("MongoID"))
                        return str;
                }
            }
        }
        catch { }

        // Попытка 3: файл локализации по templateId
        if (!string.IsNullOrEmpty(templateId))
        {
            string resolved = ResolveItemNameFromLocaleFile(templateId);
            if (!string.IsNullOrEmpty(resolved))
                return resolved;
        }

        // Попытка 4: сырой ключ
        if (!string.IsNullOrEmpty(rawKey))
            return rawKey;

        // Fallback
        string s = obj.ToString();
        if (s.Contains("ShortName") || s.Contains("LocalizedString") || (s.Length == 24 && IsHexString(s)))
            return null;
        return s;
    }

    // Устанавливает значение поля или свойства по имени.
    // Сначала пробует поле (включая приватные), затем свойство.
    // Значение автоматически конвертируется через ConvertValue.
    private static void SetFieldOrProp(object obj, Type type, string name, object value)
    {
        // Сначала пробуем поле (приватные поля в IL2CPP часто имеют публичный доступ)
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
        if (field != null)
        {
            object converted = ConvertValue(value, field.FieldType);
            field.SetValue(obj, converted);
            return;
        }

        // Затем пробуем свойство
        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
        if (prop != null && prop.CanWrite)
        {
            object converted = ConvertValue(value, prop.PropertyType);
            prop.SetValue(obj, converted);
            return;
        }
    }

    // Конвертирует значение в целевой тип.
    // Поддерживает базовые типы (int, long, double, float, string)
    // и IL2CPP-строки (по имени типа, содержащему "String").
    private static object ConvertValue(object value, Type targetType)
    {
        if (value == null) return null;

        Type sourceType = value.GetType();

        if (targetType == sourceType) return value;

        // Базовые типы
        if (targetType == typeof(int))
            return Convert.ToInt32(value);
        if (targetType == typeof(long))
            return Convert.ToInt64(value);
        if (targetType == typeof(double))
            return Convert.ToDouble(value);
        if (targetType == typeof(float))
            return Convert.ToSingle(value);
        if (targetType == typeof(string))
            return value.ToString();

        // IL2CPP-строки: если целевой тип содержит "String" в имени,
        // а исходное значение — обычная C#-строка, просто ToString()
        if (targetType.Name.Contains("String") && sourceType == typeof(string))
            return value.ToString();

        return value;
    }

    // Получает значение свойства по имени через рефлексию.
    // Ищет как публичные, так и приватные свойства.
    // Значение свойства или null.
    private static object GetPropertyValue(object obj, Type type, string propName)
    {
        try
        {
            var prop = type.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (prop != null)
                return prop.GetValue(obj);
        }
        catch { }
        return null;
    }

    // Получает значение поля по имени через рефлексию.
    // Ищет как публичные, так и приватные поля.
    // Значение поля или null.
    private static object GetFieldValue(object obj, Type type, string fieldName)
    {
        try
        {
            var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null)
                return field.GetValue(obj);
        }
        catch { }
        return null;
    }

    // Получает экземпляр Singleton-класса.
    // EFT использует обобщённый Singleton<T> для многих систем.
    // Сначала проверяет Instantiated (создан ли синглтон),
    // затем возвращает Instance экземпляр синглтона или null.
    private static object GetSingletonInstance(Type type)
    {
        try
        {
            var singletonType = typeof(Singleton<>).MakeGenericType(type);
            var instantiatedProp = singletonType.GetProperty("Instantiated", BindingFlags.Public | BindingFlags.Static);
            if (instantiatedProp != null && (bool)instantiatedProp.GetValue(null))
            {
                var instanceProp = singletonType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                if (instanceProp != null)
                    return instanceProp.GetValue(null);
            }
        }
        catch { }
        return null;
    }

    // ============================================================================
    //  ОТОБРАЖЕНИЕ УВЕДОМЛЕНИЙ В ИГРЕ
    // ============================================================================
    // Показывает внутриигровое уведомление через NotificationManager.
    // Сначала логирует сообщение, затем пытается отобразить его в игре.
    // Метод DisplayMessageNotification — статический, вызывается через рефлексию.
    // Единственная перегрузка в SPT:
    //   DisplayMessageNotification(String, ENotificationDurationType, ENotificationIconType, Nullable<Color>, Boolean) 
    // Если точный поиск перегрузки не сработал — fallback на любой
    // метод с именем DisplayMessageNotification (через GetMethods).
    internal static void ShowNotification(string message)
    {
        // Логируем сообщение в консоль BepInEx
        try
        {
            Log.LogInfo("[Уведомление] " + message);
        }
        catch { }

        // Пытаемся отобразить внутриигровое уведомление
        try
        {
            if (Singleton<NotificationManager>.Instantiated)
            {
                var nm = Singleton<NotificationManager>.Instance;
                if (nm != null)
                {
                    // Ищем все перегрузки DisplayMessageNotification
                    // Используем все binding flags: Public, NonPublic, Static, Instance
                    var allMethods = typeof(NotificationManager).GetMethods(
                            BindingFlags.Public | BindingFlags.NonPublic |
                            BindingFlags.Static | BindingFlags.Instance)
                        .Where(m => m.Name == "DisplayMessageNotification")
                        .OrderByDescending(m => m.GetParameters().Length)
                        .ToList();

                    // Создаём Il2CppSystem.Nullable<Color> с Color.white
                    // (null вызывает NRE внутри метода)
                    object colorArg = null;
                    try
                    {
                        var nullableType = typeof(Il2CppSystem.Nullable<>)
                            .MakeGenericType(typeof(UnityEngine.Color));
                        var ctor = nullableType.GetConstructor(new[] { typeof(UnityEngine.Color) });
                        if (ctor != null)
                            colorArg = ctor.Invoke(new object[] { UnityEngine.Color.white });
                    }
                    catch { }

                    foreach (var targetMethod in allMethods)
                    {
                        try
                        {
                            var targetParams = targetMethod.GetParameters();
                            object[] invokeArgs = new object[targetParams.Length];

                            for (int i = 0; i < targetParams.Length; i++)
                            {
                                var pt = targetParams[i].ParameterType;
                                if (pt == typeof(string))
                                    invokeArgs[i] = message;
                                else if (pt == typeof(ENotificationDurationType)
                                         || pt.Name == "ENotificationDurationType")
                                    invokeArgs[i] = ENotificationDurationType.Long;
                                else if (pt == typeof(ENotificationIconType)
                                         || pt.Name == "ENotificationIconType")
                                    invokeArgs[i] = ENotificationIconType.Default;
                                else if (pt == typeof(bool))
                                    invokeArgs[i] = false;
                                else if (pt.Name.Contains("Color") || pt.Name.Contains("Nullable"))
                                    invokeArgs[i] = colorArg;
                                else
                                    invokeArgs[i] = null;
                            }

                            // target = null для static, nm для instance
                            object target = targetMethod.IsStatic ? null : nm;
                            targetMethod.Invoke(target, invokeArgs);
                            return;
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
    }

    // Ищет тип по полному имени во всех загруженных сборках.
    // Нужно, потому что игровые типы EFT находятся в IL2CPP-сборках,
    // которые не видны через обычный using.
    // Найденный Type или null.
    private static Type FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = asm.GetType(fullName);
                if (type != null) return type;
            }
            catch { }
        }
        return null;
    }
}
	// MonoBehaviour-компонент для обработки нажатий клавиш.
	// Добавляется в сцену при загрузке плагина.
	// Отслеживает две клавиши:
	//   • Hotkey — быстрая продажа предмета на барахолку
	//   • ChangeCurrencyKey — переключение валюты продажи 
	// Использует _hotkeyWasPressed для предотвращения повторных срабатываний
	// при удержании клавиши (срабатывает только один раз при нажатии).
internal class FleaHelperBehaviour : MonoBehaviour
{
    // Флаг, что клавиша уже была нажата и обработана.
    // Сбрасывается при отпускании всех отслеживаемых клавиш.
    private bool _hotkeyWasPressed = false;

    // Вызывается каждый кадр. Отслеживает нажатия клавиш.
    private void Update()
    {
        // === Переключение валюты ===
        if (FleaHelperPlugin.ChangeCurrencyKey.Value != KeyCode.None &&
            Input.GetKey(FleaHelperPlugin.ChangeCurrencyKey.Value))
        {
            if (!_hotkeyWasPressed)
            {
                _hotkeyWasPressed = true;
                FleaHelperPlugin.ChangeCurrency();
            }
        }

        // === Быстрая продажа ===
        if (Input.GetKey(FleaHelperPlugin.Hotkey.Value))
        {
            if (!_hotkeyWasPressed)
            {
                _hotkeyWasPressed = true;
                FleaHelperPlugin.PostItemToFlea();
            }
        }

        // Сброс флага при отпускании всех клавиш
        if (!Input.GetKey(FleaHelperPlugin.Hotkey.Value) &&
            (FleaHelperPlugin.ChangeCurrencyKey.Value == KeyCode.None ||
             !Input.GetKey(FleaHelperPlugin.ChangeCurrencyKey.Value)))
        {
            _hotkeyWasPressed = false;
        }
    }
}

	// Валюты для выставления лотов на барахолке.
public enum EPostingCurrency
{
    RUB,  // Рубли
    USD,  // Доллары
    EUR   // Евро
}

} // конец namespace FleaHelper

// ============================================================================
//  Заглушка для Harmony-патчей.
//  В данный момент патчи не используются (TargetMethods возвращает пустой список),
//  но класс оставлен для возможных будущих патчей.
// ============================================================================
namespace FleaHelper.Patches
{
    [HarmonyPatch]
    internal class FleaHelperPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return Enumerable.Empty<MethodBase>();
        }
    }
}