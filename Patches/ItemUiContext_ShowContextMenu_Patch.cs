using System;
using System.Reflection;
using HarmonyLib;
using Comfort.Common;
using EFT.UI;
using EFT.Communications;
using EFT.InventoryLogic;
using UnityEngine;

namespace FleaHelper.Patches
{
    [HarmonyPatch(typeof(ItemUiContext), nameof(ItemUiContext.ShowContextMenu))]
    internal class ItemUiContext_ShowContextMenu_Patch
    {
        private static MethodInfo _createButtonMethod;
        private static FieldInfo _buttonsContainerField;

        private static void Prefix(ItemUiContext __instance)
        {
            var itemContext = __instance.CurrentItemContext;
            if (itemContext == null) return;

            var item = itemContext.Item;
            if (item == null) return;

            var template = item.Template;
            if (template == null) return;

            int price = template.CreditsPrice;
            string label = "Price: " + price + " RUB";

            var contextMenu = __instance.ContextMenu;
            if (contextMenu == null) return;

            if (_buttonsContainerField == null)
            {
                _buttonsContainerField = typeof(SimpleContextMenu)
                    .GetField("interactionButtonsContainer", BindingFlags.NonPublic | BindingFlags.Instance);
            }

            object buttonsContainer = _buttonsContainerField != null
                ? _buttonsContainerField.GetValue(contextMenu)
                : null;
            if (buttonsContainer == null) return;

            if (_createButtonMethod == null)
            {
                Type[] paramTypes = new Type[6];
                paramTypes[0] = typeof(string);
                paramTypes[1] = typeof(Sprite);
                paramTypes[2] = typeof(Action);
                paramTypes[3] = typeof(Action);
                paramTypes[4] = typeof(bool);
                paramTypes[5] = typeof(bool);

                _createButtonMethod = typeof(InteractionButtonsContainer)
                    .GetMethod("CreateContextButton", BindingFlags.NonPublic | BindingFlags.Instance, null, paramTypes, null);
            }

            if (_createButtonMethod != null)
            {
                Action onClick = () =>
                {
                    ShowNotification(label);
                };

                object[] invokeArgs = new object[6];
                invokeArgs[0] = label;
                invokeArgs[1] = null;
                invokeArgs[2] = onClick;
                invokeArgs[3] = null;
                invokeArgs[4] = false;
                invokeArgs[5] = false;

                _createButtonMethod.Invoke(buttonsContainer, invokeArgs);
            }
            else
            {
                ShowNotification(label);
            }
        }

        private static void ShowNotification(string message)
        {
            if (Singleton<NotificationManager>.Instantiated)
            {
                NotificationManager.DisplayMessageNotification(message);
            }
            else
            {
                Debug.Log("[FleaHelper] " + message);
            }
        }
    }
}
