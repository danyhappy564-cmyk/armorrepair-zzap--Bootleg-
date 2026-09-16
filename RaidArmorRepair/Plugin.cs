using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace RaidArmorRepair
{
    [BepInPlugin("com.pineapplelover.raidarmorrepair", "Raid Armor Repair", "1.3.0")]
    public class Plugin : BaseUnityPlugin
    {
        /// <summary>The objectives panel needs a GamePlayerOwner. It is not always up the
        /// moment the raid starts, so it is retried a few times before giving up — repairs
        /// still work without it, only the panel is lost.</summary>
        private const float OwnerRetryInterval = 1f;
        private const int OwnerRetryMax = 15;

        public static ManualLogSource Log;

        public static ConfigEntry<KeyboardShortcut> RepairKey;
        public static ConfigEntry<float> RepairPercentPerUse;
        public static ConfigEntry<float> KitResourceCostPerUse;
        public static ConfigEntry<float> RepairTickInterval;
        public static ConfigEntry<float> IntellectMaxBonusMultiplier;
        public static ConfigEntry<bool> ShowNotifications;
        public static ConfigEntry<bool> EnableMaxDurabilityDegradation;
        public static ConfigEntry<float> MaxDurabilityLossAtWorstCase;
        public static ConfigEntry<float> MaxDurabilityLossAtBestCase;
        public static ConfigEntry<float> WorstCaseReferenceEfficiency;
        public static ConfigEntry<float> DefaultArmorMaterialEfficiency;

        private float _repairTimer;
        private Player _lastKnownPlayer;
        private GamePlayerOwner _owner;
        private bool _panelShown;
        private float _ownerRetryTimer;
        private int _ownerRetryCount;

        private void Awake()
        {
            Log = Logger;

            RepairKey = Config.Bind("General", "RepairKey",
                new KeyboardShortcut(KeyCode.J),
                "레이드 중 방어구를 수리할 단축키 (기본: J). 누르고 있는 동안 계속 수리됩니다.");

            RepairPercentPerUse = Config.Bind("General", "RepairPercentPerUse", 0.3f,
                new ConfigDescription("1틱당 회복되는 최대내구도 대비 비율 (지력 보너스 적용 전 기본값)",
                    new AcceptableValueRange<float>(0.05f, 1f)));

            KitResourceCostPerUse = Config.Bind("General", "KitResourceCostPerUse", 20f,
                "1틱당 소모되는 수리킷 자원(Resource) 수치");

            RepairTickInterval = Config.Bind("General", "RepairTickInterval", 1f,
                new ConfigDescription("단축키를 누르고 있을 때 몇 초마다 한 번씩 수리할지 (첫 틱은 키를 누르는 즉시 적용됩니다)",
                    new AcceptableValueRange<float>(1f, 30f)));

            IntellectMaxBonusMultiplier = Config.Bind("General", "IntellectMaxBonusMultiplier", 0.5f,
                new ConfigDescription("지력(Intellect) 스킬이 최대일 때 1틱 회복량에 추가되는 배율 (0.5 = 최대 +50%)",
                    new AcceptableValueRange<float>(0f, 2f)));

            ShowNotifications = Config.Bind("General", "ShowNotifications", true,
                "수리 시작/종료시 화면 알림 표시 여부");

            EnableMaxDurabilityDegradation = Config.Bind("Degradation", "EnableMaxDurabilityDegradation", true,
                "수리할 때마다 최대내구도가 함께 감소하는 마모 효과를 사용할지 여부");

            MaxDurabilityLossAtWorstCase = Config.Bind("Degradation", "MaxDurabilityLossAtWorstCase", 40f,
                new ConfigDescription("지력 0 · 수리 효율이 WorstCaseReferenceEfficiency일 때의 최대내구도 감소율(%)",
                    new AcceptableValueRange<float>(0f, 100f)));

            MaxDurabilityLossAtBestCase = Config.Bind("Degradation", "MaxDurabilityLossAtBestCase", 5f,
                new ConfigDescription("지력이 최대(51)일 때 남는 최소 감소율(%). 재질/효율과 무관하게 이 값 밑으로는 내려가지 않습니다",
                    new AcceptableValueRange<float>(0f, 100f)));

            WorstCaseReferenceEfficiency = Config.Bind("Degradation", "WorstCaseReferenceEfficiency", 0.26f,
                new ConfigDescription("MaxDurabilityLossAtWorstCase 감소율의 기준이 되는 수리 효율 값",
                    new AcceptableValueRange<float>(0.01f, 10f)));

            DefaultArmorMaterialEfficiency = Config.Bind("Degradation", "DefaultArmorMaterialEfficiency", 1f,
                new ConfigDescription("방어구에서 재질을 읽지 못하거나 표에 없는 재질일 때 사용할 대체 수리 효율값",
                    new AcceptableValueRange<float>(0.01f, 10f)));

            Log.LogInfo("Raid Armor Repair loaded.");

            new Harmony("com.pineapplelover.raidarmorrepair").PatchAll();
        }

        private void Update()
        {
            if (!Singleton<GameWorld>.Instantiated)
            {
                ResetRepairState();
                return;
            }

            Player mainPlayer = Singleton<GameWorld>.Instance.MainPlayer;
            if (mainPlayer == null || mainPlayer.HealthController == null || !mainPlayer.HealthController.IsAlive)
            {
                ResetRepairState();
                return;
            }

            TrackOwner(mainPlayer);

            KeyboardShortcut key = RepairKey.Value;
            if (key.IsDown())
            {
                StartRepairSession(mainPlayer);
            }
            else if (RepairService.IsRepairing && !key.IsPressed())
            {
                // Released, or any other reason the key stopped being held.
                ResetRepairState();
            }

            if (!RepairService.IsRepairing)
            {
                return;
            }

            _repairTimer += Time.deltaTime;
            if (_repairTimer < RepairTickInterval.Value)
            {
                return;
            }

            // Subtract rather than zero, so a long frame does not silently eat progress.
            _repairTimer -= RepairTickInterval.Value;

            if (RepairService.TryRepairArmor(mainPlayer))
            {
                ShowProgressPanel(mainPlayer);
            }
            else
            {
                ResetRepairState();
            }
        }

        private void TrackOwner(Player mainPlayer)
        {
            if (_lastKnownPlayer != mainPlayer)
            {
                _lastKnownPlayer = mainPlayer;
                _owner = ResolveOwner(mainPlayer);
                _ownerRetryTimer = 0f;
                _ownerRetryCount = 0;
                return;
            }

            if (_owner != null || _ownerRetryCount >= OwnerRetryMax)
            {
                return;
            }

            _ownerRetryTimer += Time.deltaTime;
            if (_ownerRetryTimer < OwnerRetryInterval)
            {
                return;
            }

            _ownerRetryTimer = 0f;
            _ownerRetryCount++;
            _owner = ResolveOwner(mainPlayer);

            if (_owner == null && _ownerRetryCount >= OwnerRetryMax)
            {
                Log.LogWarning("[RaidArmorRepair] GamePlayerOwner를 여러 번 재시도해도 못 찾음. 이번 레이드에서는 진행률 패널을 비활성화합니다 (수리 자체는 정상 동작).");
            }
        }

        /// <summary>
        /// Checks up front rather than letting the first tick discover it 5 seconds later. Field
        /// report: a player with no repair kit in their inventory pressed the key, saw "repair
        /// starting", and then — a full RepairTickInterval later — saw the panel close with no
        /// heal applied. That is not a bug: TryRepairArmor already declines cleanly and notifies
        /// "인벤토리에 사용 가능한 방어구 수리 키트가 없습니다." But leading with "시작합니다"
        /// when the attempt is already doomed (no kit, or armor already at full durability) reads
        /// as broken even though every step behind it is correct. Checking here means a hopeless
        /// press gets exactly one message, immediately, instead of a misleading "started" followed
        /// by a delayed failure that looks like a silent close.
        /// </summary>
        private void StartRepairSession(Player player)
        {
            RepairPreview preview = RepairService.Peek(player);
            if (!preview.HasTarget)
            {
                RepairService.Notify(player, "수리할 방어구가 없거나 이미 최대 내구도입니다.");
                return;
            }

            if (!preview.HasKit)
            {
                RepairService.Notify(player, "인벤토리에 사용 가능한 방어구 수리 키트가 없습니다.");
                return;
            }

            RepairService.IsRepairing = true;
            _repairTimer = 0f;
            RepairService.NotifyRepairStart(player);

            // Field report (2026-09): holding the key felt unresponsive - press it and
            // nothing visibly happens for a full RepairTickInterval. Apply the first tick
            // immediately instead of waiting for Update()'s timer to reach it, so the key
            // repairs right away and then keeps going on the normal cadence for as long as
            // it's held. TryRepairArmor is the exact same call the periodic tick makes -
            // it already updates durability/kit resource and is safe to call here too.
            if (RepairService.TryRepairArmor(player))
            {
                ShowProgressPanel(player);
            }
            else
            {
                // The immediate tick already finished the job (or the kit ran out on it) -
                // nothing left to wait for. NotifySessionEnd only fires if that tick actually
                // healed something, so a same-press "시작합니다" + "종료: +X" pair is expected
                // and correct, not a duplicate message.
                ResetRepairState();
            }
        }

        /// <summary>
        /// The vanilla panel (EFT.UI.BattleUIPanelExtraction) closes itself once the duration
        /// passed to Show() runs out - confirmed by decompiling the client, present in both 4.0
        /// and 4.1, not something this mod controls. We re-show it every RepairTickInterval, so
        /// passing exactly that value as the duration keeps the displayed countdown on the same
        /// clock as the real time to the next tick - the number shown is the number that's true.
        ///
        /// This used to carry a flat +2s margin (see the README's "복원한 부분" / bug-fix
        /// notes) so the panel would outlive a missed refresh - an exception mid-tick, or a
        /// dropped frame - instead of the vanilla auto-close firing with no explanation.
        /// Field report (2026-09): the margin worked, but it also meant the countdown read
        /// 2 seconds ahead of the real remaining time on every single tick, not just the rare
        /// one it was guarding against - press J with 4s actually left and the panel said 6s.
        /// That guard is redundant now anyway: TryRepairArmor (RepairService.cs) already
        /// catches everything a tick can throw and returns false instead of letting it escape,
        /// which routes straight back to ResetRepairState()/ClosePanel() here rather than
        /// leaving the vanilla auto-close to fire on its own. So the margin was paying for a
        /// race that can no longer happen, at the cost of a countdown that was wrong every
        /// time it was shown. Removed; if a future change reopens that race, fix it by closing
        /// the panel explicitly on the failure path (which ResetRepairState already does),
        /// not by re-inflating the displayed number.
        /// </summary>
        private void ShowProgressPanel(Player player)
        {
            if (_owner == null)
            {
                return;
            }

            try
            {
                _owner.ShowObjectivesPanel(
                    RepairService.BuildProgressLabel(RepairService.Peek(player)),
                    RepairTickInterval.Value);
                _panelShown = true;
            }
            catch (Exception ex)
            {
                Log.LogWarning("[RaidArmorRepair] 진행률 패널 표시 실패: " + ex.Message);
            }
        }

        private void ClosePanel()
        {
            if (_panelShown && _owner != null)
            {
                try
                {
                    _owner.CloseObjectivesPanel();
                }
                catch (Exception ex)
                {
                    Log.LogWarning("[RaidArmorRepair] 진행률 패널 닫기 실패: " + ex.Message);
                }
            }

            _panelShown = false;
        }

        /// <summary>Three escalating lookups: the player object, its children, then the whole
        /// scene. Any of them can throw during a scene change, so each is guarded separately.</summary>
        private GamePlayerOwner ResolveOwner(Player player)
        {
            try
            {
                GamePlayerOwner onPlayer = player.gameObject.GetComponent<GamePlayerOwner>();
                if (onPlayer != null)
                {
                    Log.LogInfo("[RaidArmorRepair] GamePlayerOwner: player.gameObject에서 발견");
                    return onPlayer;
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("[RaidArmorRepair] GamePlayerOwner GetComponent 실패: " + ex.Message);
            }

            try
            {
                GamePlayerOwner inChildren = player.gameObject.GetComponentInChildren<GamePlayerOwner>(true);
                if (inChildren != null)
                {
                    Log.LogInfo("[RaidArmorRepair] GamePlayerOwner: player 자식 오브젝트에서 발견");
                    return inChildren;
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("[RaidArmorRepair] GamePlayerOwner GetComponentInChildren 실패: " + ex.Message);
            }

            try
            {
                GamePlayerOwner anywhere = FindObjectOfType<GamePlayerOwner>();
                if (anywhere != null)
                {
                    Log.LogInfo("[RaidArmorRepair] GamePlayerOwner: FindObjectOfType으로 발견");
                    return anywhere;
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("[RaidArmorRepair] GamePlayerOwner FindObjectOfType 실패, 진행률 패널 비활성화: " + ex.Message);
            }

            return null;
        }

        private void ResetRepairState()
        {
            bool wasRepairing = RepairService.IsRepairing;

            RepairService.IsRepairing = false;
            _repairTimer = 0f;
            ClosePanel();

            if (wasRepairing && _lastKnownPlayer != null)
            {
                RepairService.NotifySessionEnd(_lastKnownPlayer);
            }
        }
    }
}
