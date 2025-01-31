using System.Linq;
using Content.Server.GameTicking;
using Content.Shared.Access;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.PDA;
using Content.Shared.Sandbox;
using Robust.Server.Console;
using Robust.Server.Placement;
using Robust.Server.Player;
using Robust.Shared.Enums;
using Robust.Shared.Network.Messages;

namespace Content.Server.Sandbox
{
    public sealed class SandboxSystem : SharedSandboxSystem
    {
        [Dependency] private readonly IPlayerManager _playerManager = default!;
        [Dependency] private readonly IPlacementManager _placementManager = default!;
        [Dependency] private readonly IConGroupController _conGroupController = default!;
        [Dependency] private readonly IServerConsoleHost _host = default!;
        [Dependency] private readonly SharedAccessSystem _access = default!;
        [Dependency] private readonly InventorySystem _inventory = default!;
        [Dependency] private readonly ItemSlotsSystem _slots = default!;
        [Dependency] private readonly GameTicker _ticker = default!;
        [Dependency] private readonly SharedHandsSystem _handsSystem = default!;

        public event EventHandler<MsgPlacement> OnAdminPlacement = (o,e) => { };

        private bool _isSandboxEnabled;

        [ViewVariables(VVAccess.ReadWrite)]
        public bool IsSandboxEnabled
        {
            get => _isSandboxEnabled;
            set
            {
                _isSandboxEnabled = value;
                UpdateSandboxStatusForAll();
            }
        }

        public override void Initialize()
        {
            base.Initialize();
            SubscribeNetworkEvent<MsgSandboxRespawn>(SandboxRespawnReceived);
            SubscribeNetworkEvent<MsgSandboxGiveAccess>(SandboxGiveAccessReceived);
            SubscribeNetworkEvent<MsgSandboxGiveAghost>(SandboxGiveAghostReceived);
            SubscribeNetworkEvent<MsgSandboxSuicide>(SandboxSuicideReceived);

            SubscribeLocalEvent<GameRunLevelChangedEvent>(GameTickerOnOnRunLevelChanged);

            _playerManager.PlayerStatusChanged += OnPlayerStatusChanged;

            _placementManager.AllowPlacementFunc = placement =>
            {
                if (IsSandboxEnabled)
                {
                    return true;
                }

                var channel = placement.MsgChannel;
                var player = _playerManager.GetSessionByChannel(channel);

                if (_conGroupController.CanAdminPlace(player))
                {
                    OnAdminPlacement.Invoke(this,placement);
                    return true;
                }

                return false;
            };
        }

        public override void Shutdown()
        {
            base.Shutdown();
            _placementManager.AllowPlacementFunc = null;
            _playerManager.PlayerStatusChanged -= OnPlayerStatusChanged;
        }

        private void GameTickerOnOnRunLevelChanged(GameRunLevelChangedEvent obj)
        {
            // Automatically clear sandbox state when round resets.
            if (obj.New == GameRunLevel.PreRoundLobby)
            {
                IsSandboxEnabled = false;
            }
        }

        private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs e)
        {
            if (e.NewStatus != SessionStatus.Connected || e.OldStatus != SessionStatus.Connecting)
                return;

            RaiseNetworkEvent(new MsgSandboxStatus {SandboxAllowed = IsSandboxEnabled}, e.Session.ConnectedClient);
        }

        private void SandboxRespawnReceived(MsgSandboxRespawn message, EntitySessionEventArgs args)
        {
            if (!IsSandboxEnabled)
                return;

            var player = _playerManager.GetSessionByChannel(args.SenderSession.ConnectedClient);
            if (player.AttachedEntity == null) return;

            _ticker.Respawn(player);
        }

        private void SandboxGiveAccessReceived(MsgSandboxGiveAccess message, EntitySessionEventArgs args)
        {
            if (!IsSandboxEnabled)
                return;

            var player = _playerManager.GetSessionByChannel(args.SenderSession.ConnectedClient);
            if (player.AttachedEntity is not {} attached)
            {
                return;
            }

            var allAccess = PrototypeManager
                .EnumeratePrototypes<AccessLevelPrototype>()
                .Select(p => p.ID).ToArray();

            if (_inventory.TryGetSlotEntity(attached, "id", out var slotEntity))
            {
                if (HasComp<AccessComponent>(slotEntity))
                {
                    UpgradeId(slotEntity.Value);
                }
                else if (TryComp<PDAComponent>(slotEntity, out var pda))
                {
                    if (pda.ContainedID == null)
                    {
                        var newID = CreateFreshId();
                        if (TryComp<ItemSlotsComponent>(pda.Owner, out var itemSlots))
                        {
                            _slots.TryInsert(slotEntity.Value, pda.IdSlot, newID, null);
                        }
                    }
                    else
                    {
                        UpgradeId(pda.ContainedID.Owner);
                    }
                }
            }
            else if (TryComp<HandsComponent>(attached, out var hands))
            {
                var card = CreateFreshId();
                if (!_inventory.TryEquip(attached, card, "id", true, true))
                {
                    _handsSystem.PickupOrDrop(attached, card, handsComp: hands);
                }
            }

            void UpgradeId(EntityUid id)
            {
                _access.TrySetTags(id, allAccess);
            }

            EntityUid CreateFreshId()
            {
                var card = Spawn("CaptainIDCard", Transform(attached).Coordinates);
                UpgradeId(card);

                Comp<IdCardComponent>(card).FullName = MetaData(attached).EntityName;
                return card;
            }
        }

        private void SandboxGiveAghostReceived(MsgSandboxGiveAghost message, EntitySessionEventArgs args)
        {
            if (!IsSandboxEnabled)
                return;

            var player = _playerManager.GetSessionByChannel(args.SenderSession.ConnectedClient);

            _host.ExecuteCommand(player, _conGroupController.CanCommand(player, "aghost") ? "aghost" : "ghost");
        }

        private void SandboxSuicideReceived(MsgSandboxSuicide message, EntitySessionEventArgs args)
        {
            if (!IsSandboxEnabled)
                return;

            var player = _playerManager.GetSessionByChannel(args.SenderSession.ConnectedClient);
            _host.ExecuteCommand(player, "suicide");
        }

        private void UpdateSandboxStatusForAll()
        {
            RaiseNetworkEvent(new MsgSandboxStatus {SandboxAllowed = IsSandboxEnabled});
        }
    }
}
