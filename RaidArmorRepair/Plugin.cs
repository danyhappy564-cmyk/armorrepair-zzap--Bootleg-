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
    /// <summary>What happens when the player moves while holding the repair key.</summary>
    public enum MoveWhileRepairingMode
    {
        /// <summary>Walk speed is capped and sprint is blocked for as long as the repair runs.</summary>
        SlowDown,
        /// <summary>Any movement ends the repair.</summary>
        Cancel,
        /// <summary>Movement is not touched (pre-1.5.0 behaviour).</summary>
        Allow,
    }

    [BepInPlugin("com.pineapplelover.raidarmorrepair", "Raid Armor Repair", "1.5.0")]
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
        public static ConfigEntry<bool> ShowTickDetails;
        public static ConfigEntry<bool> PlayRepairSound;
        public static ConfigEntry<bool> EnableMaxDurabilityDegradation;
        public static ConfigEntry<float> MaxDurabilityLossAtWorstCase;
        public static ConfigEntry<float> MaxDurabilityLossAtBestCase;
        public static ConfigEntry<float> WorstCaseReferenceEfficiency;
        public static ConfigEntry<float> DefaultArmorMaterialEfficiency;
        public static ConfigEntry<MoveWhileRepairingMode> MoveWhileRepairing;
        public static ConfigEntry<float> RepairMoveSpeedMultiplier;

        /// <summary>
        /// Key for our entry in MovementContext's speed-limit table. The game keys that table by
        /// Player.ESpeedLimit, and Add() refuses a cause that is already present, so borrowing one
        /// of the game's own causes (Armor, Shot, ...) would either be ignored or wipe the game's
        /// limit on Remove(). An out-of-range value is safe: the table uses EnumHelper's
        /// comparer, which compares the underlying integers, and the limit is only ever folded in
        /// as "lowest value wins".
        /// </summary>
        private const Player.ESpeedLimit RepairSpeedLimitCause = (Player.ESpeedLimit)0x5241;

        /// <summary>States that count as "moving" for MoveWhileRepairingMode.Cancel.</summary>
        private static readonly EPlayerState[] MovingStates =
        {
            EPlayerState.Run,
            EPlayerState.Sprint,
            EPlayerState.ProneMove,
            EPlayerState.Jump,
            EPlayerState.Roll,
            EPlayerState.ClimbOver,
            EPlayerState.ClimbUp,
        };

        private Player _slowedPlayer;
        private float _appliedSpeedLimit;

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
                "수리 시작/종료시 화면 알림 표시 여부 (끄면 아래 ShowTickDetails 알림도 같이 꺼집니다)");

            ShowTickDetails = Config.Bind("General", "ShowTickDetails", true,
                "수리될 때마다 어느 방어구의 어느 부위가 얼마나 수리됐는지(내구도 전→후, 최대내구도 변화, 수리킷 소모/남은 양) 알림으로 표시");

            PlayRepairSound = Config.Bind("General", "PlayRepairSound", true,
                "수리될 때마다 상인/수리킷으로 수리했을 때 나는 '수리 완료' 효과음을 재생");

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

            MoveWhileRepairing = Config.Bind("Movement", "MoveWhileRepairing", MoveWhileRepairingMode.SlowDown,
                "수리 중에 움직이면 어떻게 할지. SlowDown = 느리게 걷기만 가능(달리기 불가), Cancel = 움직이면 수리 중단, Allow = 제한 없음(1.4.0 이전과 동일)");

            RepairMoveSpeedMultiplier = Config.Bind("Movement", "RepairMoveSpeedMultiplier", 0.4f,
                new ConfigDescription("SlowDown일 때 수리 중 최대 이동 속도 (평소 최대 걷기 속도 대비 비율, 0.4 = 40%)",
                    new AcceptableValueRange<float>(0.1f, 1f)));

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

            if (!ApplyMovementRules(mainPlayer))
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

        /// <summary>
        /// Runs every frame while a repair is in progress. Returns false when it ended the
        /// session (Cancel mode and the player moved).
        /// </summary>
        private bool ApplyMovementRules(Player player)
        {
            switch (MoveWhileRepairing.Value)
            {
                case MoveWhileRepairingMode.Cancel:
                    ReleaseSpeedLimit();
                    if (IsMoving(player))
                    {
                        RepairService.Notify(player, "움직여서 방어구 수리를 멈췄습니다.");
                        ResetRepairState();
                        return false;
                    }

                    return true;

                case MoveWhileRepairingMode.SlowDown:
                    ApplySpeedLimit(player);
                    return true;

                default:
                    ReleaseSpeedLimit();
                    return true;
            }
        }

        private static bool IsMoving(Player player)
        {
            try
            {
                if (player.IsSprintEnabled)
                {
                    return true;
                }

                EPlayerState state = player.CurrentState?.Name ?? EPlayerState.None;
                return Array.IndexOf(MovingStates, state) >= 0;
            }
            catch (Exception ex)
            {
                Log.LogWarning("[RaidArmorRepair] 이동 상태 확인 실패, 정지로 처리: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Caps walk speed the same way the game does for overweight (an absolute cap, scaled
        /// by MovementContext.MaxSpeed), and drops sprint: the speed-limit table only caps the
        /// walk speed, sprint has its own limit the table does not touch.
        /// </summary>
        private void ApplySpeedLimit(Player player)
        {
            try
            {
                MovementContext movement = player.MovementContext;
                if (movement == null)
                {
                    return;
                }

                float limit = RepairMoveSpeedMultiplier.Value * movement.MaxSpeed;
                if (_slowedPlayer != player || Math.Abs(_appliedSpeedLimit - limit) > 0.001f)
                {
                    ReleaseSpeedLimit();
                    movement.AddStateSpeedLimit(limit, RepairSpeedLimitCause);
                    _slowedPlayer = player;
                    _appliedSpeedLimit = limit;
                }

                if (player.IsSprintEnabled)
                {
                    player.EnableSprint(false);
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("[RaidArmorRepair] 수리 중 이동 속도 제한 실패 (수리는 계속됨): " + ex.Message);
            }
        }

        private void ReleaseSpeedLimit()
        {
            // Reference check, not Unity's ==: a destroyed player still has to be forgotten here.
            if (ReferenceEquals(_slowedPlayer, null))
            {
                return;
            }

            try
            {
                // Unity's null check: the player object may already be destroyed (raid ended).
                if (_slowedPlayer != null && _slowedPlayer.MovementContext != null)
                {
                    _slowedPlayer.MovementContext.RemoveStateSpeedLimit(RepairSpeedLimitCause);
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("[RaidArmorRepair] 이동 속도 제한 해제 실패: " + ex.Message);
            }

            _slowedPlayer = null;
            _appliedSpeedLimit = 0f;
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

            if (MoveWhileRepairing.Value == MoveWhileRepairingMode.Cancel && IsMoving(player))
            {
                RepairService.Notify(player, "움직이는 중에는 수리할 수 없습니다. 멈춘 상태에서 다시 눌러주세요.");
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
            ReleaseSpeedLimit();

            if (wasRepairing && _lastKnownPlayer != null)
            {
                RepairService.NotifySessionEnd(_lastKnownPlayer);
            }
        }
    }
}
