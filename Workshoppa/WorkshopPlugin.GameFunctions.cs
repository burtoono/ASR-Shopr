﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using LLib.GameUI;
using Workshoppa.GameData;

namespace Workshoppa;

partial class WorkshopPlugin
{
    private unsafe void InteractWithTarget(GameObject obj)
    {
        _pluginLog.Information($"Setting target to {obj}");
        /*
        if (_targetManager.Target == null || _targetManager.Target != obj)
        {
            _targetManager.Target = obj;
        }
*/
        TargetSystem.Instance()->InteractWithObject(
            (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address, false);
    }

    private float GetDistanceToEventObject(IReadOnlyList<uint> npcIds, out GameObject? o)
    {
        Vector3? localPlayerPosition = _clientState.LocalPlayer?.Position;
        if (localPlayerPosition != null)
        {
            foreach (var obj in _objectTable)
            {
                if (obj.ObjectKind == ObjectKind.EventObj)
                {
                    if (npcIds.Contains(GetNpcId(obj)))
                    {
                        o = obj;
                        float distance = Vector3.Distance(localPlayerPosition.Value,
                            obj.Position + new Vector3(0, -2, 0));
                        if (distance > 0.01)
                            return distance;
                    }
                }
            }
        }

        o = null;
        return float.MaxValue;
    }

    private unsafe uint GetNpcId(GameObject obj)
    {
        return ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address)->GetNpcID();
    }


    private unsafe AtkUnitBase* GetCompanyCraftingLogAddon()
    {
        if (_gameGui.TryGetAddonByName<AtkUnitBase>("CompanyCraftRecipeNoteBook", out var addon) &&
            LAddon.IsAddonReady(addon))
            return addon;

        return null;
    }

    /// <summary>
    /// This actually has different addons depending on the craft, e.g. SubmarinePartsMenu.
    /// </summary>
    /// <returns></returns>
    private unsafe AtkUnitBase* GetMaterialDeliveryAddon()
    {
        var agentInterface = AgentModule.Instance()->GetAgentByInternalId(AgentId.CompanyCraftMaterial);
        if (agentInterface != null && agentInterface->IsAgentActive())
        {
            var addonId = agentInterface->GetAddonID();
            if (addonId == 0)
                return null;

            AtkUnitBase* addon = LAddon.GetAddonById(addonId);
            if (LAddon.IsAddonReady(addon))
                return addon;
        }

        return null;
    }

    private unsafe bool SelectSelectString(string marker, int choice, Predicate<string> predicate)
    {
        if (_gameGui.TryGetAddonByName<AddonSelectString>("SelectString", out var addonSelectString) &&
            LAddon.IsAddonReady(&addonSelectString->AtkUnitBase))
        {
            int entries = addonSelectString->PopupMenu.PopupMenu.EntryCount;
            if (entries < choice)
                return false;

            var textPointer = addonSelectString->PopupMenu.PopupMenu.EntryNames[choice];
            if (textPointer == null)
                return false;

            var text = MemoryHelper.ReadSeStringNullTerminated((nint)textPointer).ToString();
            _pluginLog.Verbose($"SelectSelectString for {marker}, Choice would be '{text}'");
            if (predicate(text))
            {
                addonSelectString->AtkUnitBase.FireCallbackInt(choice);
                return true;
            }
        }

        return false;
    }

    private unsafe bool SelectSelectYesno(int choice, Predicate<string> predicate)
    {
        if (_gameGui.TryGetAddonByName<AddonSelectYesno>("SelectYesno", out var addonSelectYesno) &&
            LAddon.IsAddonReady(&addonSelectYesno->AtkUnitBase))
        {
            var text = MemoryHelper.ReadSeString(&addonSelectYesno->PromptText->NodeText).ToString();
            text = text.Replace("\n", "").Replace("\r", "");
            if (predicate(text))
            {
                _pluginLog.Information($"Selecting choice {choice} for '{text}'");
                addonSelectYesno->AtkUnitBase.FireCallbackInt(choice);
                return true;
            }
            else
            {
                _pluginLog.Verbose($"Text {text} does not match");
            }
        }

        return false;
    }

    private unsafe CraftState? ReadCraftState(AtkUnitBase* addonMaterialDelivery)
    {
        try
        {
            var atkValues = addonMaterialDelivery->AtkValues;
            if (addonMaterialDelivery->AtkValuesCount == 157 && atkValues != null)
            {
                uint resultItem = atkValues[0].UInt;
                uint stepsComplete = atkValues[6].UInt;
                uint stepsTotal = atkValues[7].UInt;
                uint listItemCount = atkValues[11].UInt;
                List<CraftItem> items = Enumerable.Range(0, (int)listItemCount)
                    .Select(i => new CraftItem
                    {
                        ItemId = atkValues[12 + i].UInt,
                        IconId = atkValues[24 + i].UInt,
                        ItemName = atkValues[36 + i].ReadAtkString(),
                        CrafterIconId = atkValues[48 + i].Int,
                        ItemCountPerStep = atkValues[60 + i].UInt,
                        ItemCountNQ = atkValues[72 + i].UInt,
                        ItemCountHQ = ParseAtkItemCountHq(atkValues[84 + i]),
                        Experience = atkValues[96 + i].UInt,
                        StepsComplete = atkValues[108 + i].UInt,
                        StepsTotal = atkValues[120 + i].UInt,
                        Finished = atkValues[132 + i].UInt > 0,
                        CrafterMinimumLevel = atkValues[144 + i].UInt,
                    })
                    .ToList();

                return new CraftState
                {
                    ResultItem = resultItem,
                    StepsComplete = stepsComplete,
                    StepsTotal = stepsTotal,
                    Items = items,
                };
            }
        }
        catch (Exception e)
        {
            _pluginLog.Warning(e, "Could not parse CompanyCraftMaterial info");
        }

        return null;
    }

    private uint ParseAtkItemCountHq(AtkValue atkValue)
    {
        // NQ / HQ string
        // I have no clue, but it doesn't seme like the available HQ item count is strored anywhere in the atkvalues??
        string? s = atkValue.ReadAtkString();
        if (s != null)
        {
            var parts = s.Replace("\ue03c", "").Split('/');
            if (parts.Length > 1)
            {
                return uint.Parse(parts[1].Replace(",", "").Replace(".", "").Trim());
            }
        }

        return 0;
    }

    private unsafe bool HasItemInSingleSlot(uint itemId, uint count)
    {
        var inventoryManger = InventoryManager.Instance();
        if (inventoryManger == null)
            return false;

        for (InventoryType t = InventoryType.Inventory1; t <= InventoryType.Inventory4; ++t)
        {
            var container = inventoryManger->GetInventoryContainer(t);
            for (int i = 0; i < container->Size; ++i)
            {
                var item = container->GetInventorySlot(i);
                if (item == null)
                    continue;

                if (item->ItemID == itemId && item->Quantity >= count)
                    return true;
            }
        }

        return false;
    }

    public bool HasFreeInventorySlot() => GetFreeInventorySlots() > 0;

    public unsafe int GetFreeInventorySlots()
    {
        var inventoryManger = InventoryManager.Instance();
        if (inventoryManger == null)
            return 0;

        int count = 0;
        for (InventoryType t = InventoryType.Inventory1; t <= InventoryType.Inventory4; ++t)
        {
            var container = inventoryManger->GetInventoryContainer(t);
            for (int i = 0; i < container->Size; ++i)
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->ItemID == 0)
                    ++count;
            }
        }

        return count;
    }

    public unsafe int DetermineMaxStackSize(uint itemId)
    {
        var inventoryManger = InventoryManager.Instance();
        if (inventoryManger == null)
            return 0;

        int max = 0;
        for (InventoryType t = InventoryType.Inventory1; t <= InventoryType.Inventory4; ++t)
        {
            var container = inventoryManger->GetInventoryContainer(t);
            for (int i = 0; i < container->Size; ++i)
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->ItemID == 0)
                    return 99;

                if (item->ItemID == itemId)
                {
                    max += (999 - (int)item->Quantity);
                    if (max >= 99)
                        break;
                }
            }
        }

        return Math.Min(99, max);
    }
}
