using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.UI;

namespace RaidArmorRepair
{
    /// <summary>
    /// All of the actual repairing. Kept static because there is exactly one repair session
    /// at a time and the Harmony trigger patch needs to read its state.
    /// </summary>
    public static class RepairService
    {
        /// <summary>Intellect is capped at 51 in EFT; the bonus scales linearly to that.</summary>
        private const float MaxIntellectSkillLevel = 51f;

        /// <summary>Where worn armour can be. Plates inside these are handled too.</summary>
        private static readonly EquipmentSlot[] ArmorSlots =
        {
            EquipmentSlot.ArmorVest,
            EquipmentSlot.TacticalVest,
            EquipmentSlot.Headwear,
        };

        /// <summary>Where a repair kit may be carried. Searched recursively.</summary>
        private static readonly EquipmentSlot[] ContainerSlotIds =
        {
            EquipmentSlot.Backpack,
            EquipmentSlot.TacticalVest,
            EquipmentSlot.Pockets,
            EquipmentSlot.SecuredContainer,
        };

        /// <summary>
        /// Repair efficiency per armour material — the same numbers EFT uses out of raid.
        /// Higher is easier to repair, and it feeds the max-durability loss: ceramic and
        /// glass wear out fast, steel and UHMWPE barely at all.
        /// </summary>
        /// <remarks>Keyed by the 4.1 EArmorMaterial enum (UHMWPE, Aramid, Combined, Titan,
        /// Aluminium, ArmoredSteel, Ceramic, Glass); the extra spellings are harmless leftovers
        /// from when this was read by reflection.</remarks>
        private static readonly Dictionary<string, float> ArmorMaterialEfficiency =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                { "Aramid", 1f },
                { "UHMWPE", 3f },
                { "Combined", 0.4f },
                { "CombinedMaterials", 0.4f },
                { "Titan", 0.63f },
                { "Titanium", 0.63f },
                { "Aluminium", 0.63f },
                { "Aluminum", 0.63f },
                { "ArmoredSteel", 3f },
                { "Armor_steel", 3f },
                { "Steel", 3f },
                { "Ceramic", 0.26f },
                { "Glass", 0.15f },
            };

        /// <summary>Korean names for the armour slots plates and soft inserts sit in. Slot ids
        /// come from the item database; anything not listed falls back to the raw id.</summary>
        private static readonly Dictionary<string, string> ArmorSlotNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Front_plate", "앞판" },
                { "Back_plate", "뒷판" },
                { "Left_side_plate", "왼쪽 측면판" },
                { "Right_side_plate", "오른쪽 측면판" },
                { "Soft_armor_front", "앞 연질" },
                { "Soft_armor_back", "뒤 연질" },
                { "Soft_armor_left", "왼쪽 연질" },
                { "Soft_armor_right", "오른쪽 연질" },
                { "Collar", "목" },
                { "Groin", "골반(앞)" },
                { "Groin_back", "골반(뒤)" },
                { "Shoulder_l", "왼쪽 어깨" },
                { "Shoulder_r", "오른쪽 어깨" },
                { "Helmet_top", "정수리" },
                { "Helmet_back", "뒤통수" },
                { "Helmet_ears", "귀" },
                { "Helmet_eyes", "눈" },
                { "Helmet_jaw", "턱" },
            };

        public static bool IsRepairing;
        public static float SessionKitResourceUsed;
        public static float SessionDurabilityRepaired;

        /// <summary>Durability restored per part this session, in the order first repaired.</summary>
        private static readonly List<KeyValuePair<string, float>> SessionParts = new List<KeyValuePair<string, float>>();

        private static bool _loggedSoundFailure;

        public static void NotifyRepairStart(Player player)
        {
            ResetSession();
            Notify(player, $"방어구 수리를 시작합니다. 단축키를 누르고 있으면 {Plugin.RepairTickInterval.Value:0.#}초마다 수리됩니다.");
        }

        public static void NotifySessionEnd(Player player)
        {
            if (SessionKitResourceUsed <= 0f && SessionDurabilityRepaired <= 0f)
            {
                return;
            }

            string parts = SessionParts.Count > 0
                ? " [" + string.Join(", ", SessionParts.Select(p => $"{p.Key} +{p.Value:0.#}")) + "]"
                : "";

            Notify(player, $"방어구 수리 종료: 내구도 +{SessionDurabilityRepaired:0.#} 회복, 수리킷 {SessionKitResourceUsed:0.#} 소모{parts}");
            ResetSession();
        }

        private static void ResetSession()
        {
            SessionKitResourceUsed = 0f;
            SessionDurabilityRepaired = 0f;
            SessionParts.Clear();
        }

        private static void AddSessionPart(string label, float heal)
        {
            for (int i = 0; i < SessionParts.Count; i++)
            {
                if (SessionParts[i].Key == label)
                {
                    SessionParts[i] = new KeyValuePair<string, float>(label, SessionParts[i].Value + heal);
                    return;
                }
            }

            SessionParts.Add(new KeyValuePair<string, float>(label, heal));
        }

        // ---- skill ---------------------------------------------------------

        private static float GetIntellectMultiplier(Player player) =>
            1f + Normalised(GetIntellectLevel(player)) * Plugin.IntellectMaxBonusMultiplier.Value;

        private static float Normalised(float intellectLevel) =>
            Math.Max(0f, Math.Min(1f, intellectLevel / MaxIntellectSkillLevel));

        private static float GetIntellectLevel(Player player)
        {
            try
            {
                // Skill was SkillClass before 4.1 deobfuscated the client.
                Skill intellect = player?.Profile?.Skills?.Intellect;
                return intellect == null ? 0f : intellect.Level;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[RaidArmorRepair] 지력 스킬 조회 실패, 레벨 0으로 처리: " + ex.Message);
                return 0f;
            }
        }

        /// <summary>
        /// How much max durability one repair costs, as a percent. Worst case is intellect 0
        /// on a low-efficiency material; the loss shrinks with both intellect and material
        /// efficiency, and never drops below the configured best case.
        /// </summary>
        private static float GetMaxDurabilityLossPercent(float intellectLevel, float materialEfficiency)
        {
            if (materialEfficiency <= 0f)
            {
                materialEfficiency = 0.01f;
            }

            float distanceFromMaxIntellect = 1f - Normalised(intellectLevel);
            float best = Plugin.MaxDurabilityLossAtBestCase.Value;
            float worst = Plugin.MaxDurabilityLossAtWorstCase.Value;

            float loss = best + (worst - best)
                * (Plugin.WorstCaseReferenceEfficiency.Value / materialEfficiency)
                * distanceFromMaxIntellect;

            return Math.Max(0f, Math.Min(100f, loss));
        }

        /// <summary>
        /// Reads the armour's material off its component template. This used to go through
        /// reflection on Item.Template with "Material" as a second guess — but in 4.1 that name
        /// is the hit-effect MaterialType (BodyArmor, Helmet, ...), not the armour material, so
        /// the fallback could only ever produce a wrong lookup. 4.1 exposes the real value
        /// directly as IArmorComponentTemplate.ArmorMaterial (EArmorMaterial).
        /// </summary>
        private static float GetArmorMaterialEfficiency(ArmorComponent armorComponent)
        {
            try
            {
                if (armorComponent?.Template != null)
                {
                    string material = armorComponent.Template.ArmorMaterial.ToString();
                    if (ArmorMaterialEfficiency.TryGetValue(material, out float efficiency))
                    {
                        return efficiency;
                    }

                    Plugin.Log.LogWarning($"[RaidArmorRepair] 표에 없는 방어구 재질 '{material}', 기본 효율({Plugin.DefaultArmorMaterialEfficiency.Value})을 사용합니다.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[RaidArmorRepair] 방어구 재질 조회 실패: " + ex.Message);
            }

            return Plugin.DefaultArmorMaterialEfficiency.Value;
        }

        /// <summary>
        /// Wear scales with how much of a full tick was actually used. Before 1.4.0 a tick that
        /// restored a single point (armour at 99/100, or a nearly empty kit) cost the same full
        /// MaxDurabilityLoss percent as a full tick — so topping off near-full armour with
        /// default settings could take it from 99/100 to 60/60.
        /// </summary>
        private static float ScaleLossByHeal(float lossPercent, float heal, float healPerTick) =>
            healPerTick > 0f ? lossPercent * Math.Max(0f, Math.Min(1f, heal / healPerTick)) : 0f;

        // ---- preview -------------------------------------------------------

        /// <summary>Works out what the next tick would do, without changing anything.</summary>
        public static RepairPreview Peek(Player player)
        {
            var preview = default(RepairPreview);
            InventoryEquipment equipment = player.Inventory.Equipment;

            if (!TryFindDamagedArmorInEquipment(equipment, out Item targetArmor, out ArmorComponent armorComponent, out string label, false))
            {
                return preview;
            }

            preview.HasTarget = true;
            preview.TargetArmor = targetArmor;
            preview.TargetComponent = armorComponent;
            preview.TargetLabel = label;

            RepairableComponent repairable = armorComponent.Repairable;
            preview.MaxDurability = repairable.MaxDurability;
            preview.CurrentDurability = repairable.Durability;
            preview.MissingDurability = preview.MaxDurability - preview.CurrentDurability;

            if (!TryFindRepairKit(equipment, targetArmor, out Item kitItem, out RepairKitComponent kitComponent, false))
            {
                return preview;
            }

            preview.HasKit = true;
            preview.KitItem = kitItem;
            preview.KitComponent = kitComponent;

            float intellectLevel = GetIntellectLevel(player);
            float intellectMultiplier = 1f + Normalised(intellectLevel) * Plugin.IntellectMaxBonusMultiplier.Value;

            float healPerTick = preview.MaxDurability * Plugin.RepairPercentPerUse.Value * intellectMultiplier;
            float kitCost = Plugin.KitResourceCostPerUse.Value;
            float durabilityPerResource = kitCost > 0f ? healPerTick / kitCost : 0f;
            float healTheKitCanAfford = durabilityPerResource > 0f ? kitComponent.Resource * durabilityPerResource : 0f;

            float healIgnoringKit = Math.Max(0f, Math.Min(healPerTick, preview.MissingDurability));
            preview.ExpectedHeal = Math.Max(0f, Math.Min(healPerTick, Math.Min(preview.MissingDurability, healTheKitCanAfford)));
            preview.LimitedByKit = preview.ExpectedHeal < healIgnoringKit - 0.01f;

            if (Plugin.EnableMaxDurabilityDegradation.Value && preview.ExpectedHeal > 0f)
            {
                preview.ExpectedMaxDurabilityLossPercent = ScaleLossByHeal(
                    GetMaxDurabilityLossPercent(intellectLevel, GetArmorMaterialEfficiency(armorComponent)),
                    preview.ExpectedHeal, healPerTick);
            }

            return preview;
        }

        /// <summary>
        /// The objectives-panel label. The trailing "{0:F1}" is a placeholder the game fills
        /// with the remaining countdown, so it has to survive into the returned string.
        /// </summary>
        public static string BuildProgressLabel(RepairPreview preview)
        {
            if (!preview.HasTarget)
            {
                return "수리할 방어구 없음 {0:F1}";
            }

            if (!preview.HasKit)
            {
                return "수리킷 없음 {0:F1}";
            }

            float projected = preview.CurrentDurability + preview.ExpectedHeal;
            float percent = preview.MaxDurability > 0f ? Math.Min(100f, projected / preview.MaxDurability * 100f) : 0f;
            string kitNote = preview.LimitedByKit ? " · 키트 부족" : "";
            string wearNote = preview.ExpectedMaxDurabilityLossPercent > 0.05f
                ? $" · 최대내구도 -{preview.ExpectedMaxDurabilityLossPercent:0.#}%"
                : "";

            string target = string.IsNullOrEmpty(preview.TargetLabel) ? "방어구" : preview.TargetLabel;
            return $"{target} 수리 중 (예상 {percent:0}%{kitNote}{wearNote}) " + "{0:F1}s";
        }

        // ---- the tick ------------------------------------------------------

        /// <summary>Applies one repair tick. Returns false when the session should stop —
        /// nothing damaged left, or nothing to repair it with.</summary>
        /// <remarks>
        /// Everything below used to run bare. If any of it threw, the exception unwound straight
        /// out through Plugin.Update() — Unity logs it once and keeps the MonoBehaviour alive,
        /// but that Update() call stops dead at the throw point. Neither the success branch
        /// (ShowProgressPanel) nor the failure branch (ResetRepairState) in the caller ever runs,
        /// so nothing closes the panel and nothing tells the player anything went wrong. The
        /// progress panel is a vanilla EFT.UI.BattleUIPanelExtraction; its own Show(text, duration)
        /// starts a coroutine that calls Close() by itself once `duration` elapses (confirmed by
        /// decompiling both the 4.0 and 4.1 client — not a porting regression, just how it always
        /// worked). With nobody refreshing it, that is exactly what a silent exception here looks
        /// like from the player's side: the "repair starting" notification fires, the panel shows,
        /// and a few seconds later the panel closes on its own timer with no heal ever applied and
        /// no error notification — because the two explicit failure notifies below never had a
        /// chance to fire.
        /// </remarks>
        public static bool TryRepairArmor(Player player)
        {
            try
            {
                return TryRepairArmorInternal(player);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[RaidArmorRepair] 수리 틱 처리 중 예외 발생: " + ex);
                Notify(player, "방어구 수리 중 오류가 발생해 이번 시도는 취소되었습니다. (로그 확인 필요)");
                return false;
            }
        }

        private static bool TryRepairArmorInternal(Player player)
        {
            InventoryEquipment equipment = player.Inventory.Equipment;

            if (!TryFindDamagedArmorInEquipment(equipment, out Item armorItem, out ArmorComponent armorComponent, out string label, Plugin.DebugLog.Value))
            {
                Notify(player, "수리할 방어구가 없거나 이미 최대 내구도입니다.");
                return false;
            }

            if (!TryFindRepairKit(equipment, armorItem, out Item kitItem, out RepairKitComponent kitComponent, Plugin.DebugLog.Value))
            {
                Notify(player, "인벤토리에 사용 가능한 방어구 수리 키트가 없습니다.");
                return false;
            }

            RepairableComponent repairable = armorComponent.Repairable;
            float maxDurability = repairable.MaxDurability;
            float durabilityBefore = repairable.Durability;
            float intellectMultiplier = GetIntellectMultiplier(player);

            float missing = maxDurability - repairable.Durability;
            float healPerTick = maxDurability * Plugin.RepairPercentPerUse.Value * intellectMultiplier;
            float kitCost = Plugin.KitResourceCostPerUse.Value;
            float durabilityPerResource = kitCost > 0f ? healPerTick / kitCost : 0f;
            float healTheKitCanAfford = durabilityPerResource > 0f ? kitComponent.Resource * durabilityPerResource : 0f;

            // Whichever runs out first: the tick budget, the damage, or the kit.
            float heal = Math.Min(healPerTick, Math.Min(missing, healTheKitCanAfford));
            if (heal < 0f)
            {
                heal = 0f;
            }

            // Charge for exactly what was healed, not for a full tick.
            float resourceUsed = durabilityPerResource > 0f ? heal / durabilityPerResource : 0f;

            repairable.Durability += heal;
            kitComponent.Resource -= resourceUsed;

            float lossPercent = 0f;
            if (Plugin.EnableMaxDurabilityDegradation.Value && heal > 0f)
            {
                lossPercent = ScaleLossByHeal(
                    GetMaxDurabilityLossPercent(GetIntellectLevel(player), GetArmorMaterialEfficiency(armorComponent)),
                    heal, healPerTick);
                float lossPoints = maxDurability * (lossPercent / 100f);
                if (lossPoints > 0f)
                {
                    // Never below 1, and current durability follows the ceiling down.
                    float newMax = Math.Max(1f, maxDurability - lossPoints);
                    if (repairable.Durability > newMax)
                    {
                        repairable.Durability = newMax;
                    }

                    repairable.MaxDurability = newMax;
                }
            }

            SessionKitResourceUsed += resourceUsed;
            SessionDurabilityRepaired += heal;
            AddSessionPart(label, heal);

            // Durability/resource are already updated above regardless of what happens here — this
            // is only telling the UI to redraw. armorItem can be a plate nested inside the vest's
            // ArmorHolderComponent rather than a top-level equipped item; raising the event on a
            // nested item is the one part of this tick that has ever been iffy across EFT builds,
            // so a stale tooltip is an acceptable failure mode here, a lost repair is not.
            try
            {
                armorItem.RaiseRefreshEvent(false, false);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[RaidArmorRepair] RaiseRefreshEvent 실패 (내구도는 이미 적용됨, UI만 안 갱신될 수 있음): " + ex.Message);
            }

            // The kit's remaining resource changed too; without this its tooltip stays stale.
            try
            {
                kitItem.RaiseRefreshEvent(false, false);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[RaidArmorRepair] 수리킷 RaiseRefreshEvent 실패 (자원은 이미 차감됨): " + ex.Message);
            }

            Plugin.Debug($"틱 적용: {label} +{heal:0.#} 내구도, 수리킷 -{resourceUsed:0.#}, 지력 보너스 x{intellectMultiplier:0.00}, 최대내구도 -{lossPercent:0.#}%");

            if (heal > 0f)
            {
                if (Plugin.ShowTickDetails.Value)
                {
                    Notify(player, BuildTickMessage(label, durabilityBefore, repairable.Durability, maxDurability,
                        repairable.MaxDurability, resourceUsed, kitComponent.Resource));
                }

                PlayRepairSound();
            }

            return HasAnyDamagedArmor(equipment);
        }

        // ---- lookups -------------------------------------------------------

        private static bool HasAnyDamagedArmor(InventoryEquipment equipment) =>
            TryFindDamagedArmorInEquipment(equipment, out _, out _, out _, false);

        private static bool TryFindDamagedArmorInEquipment(
            InventoryEquipment equipment, out Item targetItem, out ArmorComponent targetComponent, out string label, bool verboseLog)
        {
            foreach (EquipmentSlot slotId in ArmorSlots)
            {
                Item item = equipment.GetSlot(slotId)?.ContainedItem;
                if (item == null)
                {
                    continue;
                }

                if (verboseLog)
                {
                    Plugin.Debug($"슬롯 {slotId}: {item.TemplateId} 확인 중...");
                }

                if (TryFindDamagedArmor(item, out targetItem, out targetComponent, out label))
                {
                    return true;
                }
            }

            targetItem = null;
            targetComponent = null;
            label = null;
            return false;
        }

        /// <summary>The item itself if it is damaged armour, otherwise the first damaged
        /// plate inside it.</summary>
        private static bool TryFindDamagedArmor(Item equippedItem, out Item targetItem, out ArmorComponent targetComponent, out string label)
        {
            ArmorComponent armor = equippedItem.GetItemComponent<ArmorComponent>();
            if (armor != null && IsDamaged(armor))
            {
                targetItem = equippedItem;
                targetComponent = armor;
                label = SafeShortName(equippedItem);
                return true;
            }

            ArmorHolderComponent holder = equippedItem.GetItemComponent<ArmorHolderComponent>();
            if (holder != null)
            {
                // Same walk as holder.ArmorPlates (every ArmorSlot, every ArmorPlate in it), done
                // by hand so the slot is known for the label. ArmorPlate was ArmorPlateItemClass
                // before 4.1 deobfuscated the client.
                foreach (ArmorSlot slot in holder.ArmorSlots)
                {
                    foreach (Item slotItem in slot.Items)
                    {
                        if (!(slotItem is ArmorPlate plate))
                        {
                            continue;
                        }

                        ArmorComponent plateArmor = plate.GetItemComponent<ArmorComponent>();
                        if (plateArmor != null && IsDamaged(plateArmor))
                        {
                            targetItem = plate;
                            targetComponent = plateArmor;
                            label = DescribePlate(equippedItem, slot, plate);
                            return true;
                        }
                    }
                }
            }

            targetItem = null;
            targetComponent = null;
            label = null;
            return false;
        }

        /// <summary>"6B13 앞판(SAPI)" for a removable plate, "6B13 앞 연질" for a built-in
        /// insert — the insert's own name just repeats the slot.</summary>
        private static string DescribePlate(Item holderItem, Slot slot, ArmorPlate plate)
        {
            string slotName = slot?.ID != null && ArmorSlotNames.TryGetValue(slot.ID, out string ko) ? ko : slot?.ID ?? "?";
            string holderName = SafeShortName(holderItem);

            return plate is BuiltInInserts
                ? $"{holderName} {slotName}"
                : $"{holderName} {slotName}({SafeShortName(plate)})";
        }

        private static string SafeShortName(Item item)
        {
            try
            {
                string name = item.LocalizedShortName();
                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }
            }
            catch (Exception)
            {
                // Locale not loaded yet — the template id still tells the log reader what it was.
            }

            return item.TemplateId.ToString();
        }

        private static string BuildTickMessage(string label, float before, float after, float oldMax, float newMax,
            float kitUsed, float kitLeft)
        {
            string maxNote = newMax < oldMax - 0.05f ? $" · 최대 {oldMax:0.#}→{newMax:0.#}" : "";
            string doneNote = after >= newMax - 0.01f ? " · 완료" : "";
            return $"수리: {label} {before:0.#}→{after:0.#}/{newMax:0.#} (+{after - before:0.#}){maxNote}{doneNote} · 키트 -{kitUsed:0.#} (남음 {kitLeft:0})";
        }

        /// <summary>The same "repair complete" clip the trader/repair-kit window plays
        /// (RepairWindow.RepairItem -> GUISounds.PlayUISound(EUISoundType.RepairComplete)).
        /// GUISounds is created once at app start (ClientApplication) and lives through raids.</summary>
        private static void PlayRepairSound()
        {
            if (!Plugin.PlayRepairSound.Value)
            {
                return;
            }

            try
            {
                if (Singleton<GUISounds>.Instantiated)
                {
                    Singleton<GUISounds>.Instance.PlayUISound(EUISoundType.RepairComplete);
                }
            }
            catch (Exception ex)
            {
                if (!_loggedSoundFailure)
                {
                    _loggedSoundFailure = true;
                    Plugin.Log.LogWarning("[RaidArmorRepair] 수리 효과음 재생 실패 (수리는 정상 적용됨): " + ex.Message);
                }
            }
        }

        /// <summary>The 0.01 slack keeps float noise from making a full-durability plate
        /// look repairable forever.</summary>
        private static bool IsDamaged(ArmorComponent armor) =>
            armor.Repairable.Durability < armor.Repairable.MaxDurability - 0.01f;

        private static bool TryFindRepairKit(
            InventoryEquipment equipment, Item target, out Item repairKitItem, out RepairKitComponent kitComponent, bool verboseLog)
        {
            var carried = new List<Item>();

            foreach (EquipmentSlot slotId in ContainerSlotIds)
            {
                Item container = equipment.GetSlot(slotId)?.ContainedItem;
                if (container == null)
                {
                    continue;
                }

                if (verboseLog)
                {
                    Plugin.Debug($"컨테이너 슬롯 확인: {slotId} -> {container.TemplateId}");
                }

                CollectAllContainedItems(container, carried);
            }

            if (verboseLog)
            {
                Plugin.Debug($"컨테이너 내부에서 총 {carried.Count}개 아이템 발견.");
                foreach (Item item in carried)
                {
                    RepairKitComponent kit = item.GetItemComponent<RepairKitComponent>();
                    if (kit != null)
                    {
                        string usable = KitCanRepair(item, target) ? "사용 가능" : "이 방어구에 못 씀(무기 수리킷 등) — 건너뜀";
                        Plugin.Debug($"  - 수리킷 발견: {item.TemplateId}, 자원 {kit.Resource:0.#}, {usable}");
                    }
                }
            }

            repairKitItem = carried.FirstOrDefault(item =>
            {
                RepairKitComponent kit = item.GetItemComponent<RepairKitComponent>();
                return kit != null && kit.Resource > 0f && KitCanRepair(item, target);
            });

            kitComponent = repairKitItem?.GetItemComponent<RepairKitComponent>();
            return repairKitItem != null;
        }

        /// <summary>
        /// Weapon repair kits carry a RepairKitComponent too, so before 1.4.0 a weapon kit was
        /// happily spent on armour. RepairKit.CanRepair is the vanilla check the repair window
        /// uses (the kit's repair strategies vs. the item's template). If it throws — globals
        /// not ready — the kit is allowed rather than blocking every repair.
        /// </summary>
        private static bool KitCanRepair(Item kitItem, Item target)
        {
            if (target == null || !(kitItem is RepairKit kit))
            {
                return true;
            }

            try
            {
                return kit.CanRepair(target);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[RaidArmorRepair] 수리킷 대상 확인 실패, 허용으로 처리: " + ex.Message);
                return true;
            }
        }

        /// <summary>Everything inside a container, at any depth — a kit in a case in a
        /// backpack still counts.</summary>
        private static void CollectAllContainedItems(Item item, List<Item> results)
        {
            if (!(item is CompoundItem compound))
            {
                return;
            }

            foreach (IContainer container in compound.Containers)
            {
                foreach (Item contained in container.Items)
                {
                    if (contained == null)
                    {
                        continue;
                    }

                    results.Add(contained);
                    CollectAllContainedItems(contained, results);
                }
            }
        }

        public static void Notify(Player player, string message)
        {
            Plugin.Debug("알림: " + message);

            if (!Plugin.ShowNotifications.Value)
            {
                return;
            }

            try
            {
                // NotificationManagerClass before 4.1 deobfuscated the client.
                NotificationManager.DisplayMessageNotification(message, 0, 0, null);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("알림 표시 실패: " + ex.Message);
            }
        }
    }
}
