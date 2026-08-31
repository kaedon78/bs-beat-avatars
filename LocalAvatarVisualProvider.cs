using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BeatSaber.AvatarCore;
using BeatSaber.BeatAvatarAdapter;
using BeatSaber.BeatAvatarSDK;

namespace BeatAvatarBody
{
    /// <summary>
    /// Supplies the player's OWN saved avatar appearance -- the one edited in the game's avatar
    /// editor and stored in AvatarData.dat -- and pushes an update when they change it.
    ///
    /// The obvious hook, <see cref="IAvatarSystem.avatarDidChangeEvent"/>, is DEAD. Its only raiser
    /// is AvatarSystem.RaiseAvatarDidChangeEvent, which is protected and, measured 2026-08-31, has
    /// no caller in any of the 255 game assemblies -- the string occurs in
    /// BeatSaber.AvatarCore.dll, where it is defined, and nowhere else. Subscribing to it compiles,
    /// runs, and silently never fires: the interface advertises a notification the game never
    /// sends.
    ///
    /// The signal that does fire is <see cref="AvatarDataModel.didChangeAvatarDataEvent"/>, raised
    /// by ReportAvatarChanged from the avatarData setter, which is what the avatar editor itself
    /// listens to. We take both, so this keeps working if a later version starts raising the
    /// system-level one.
    /// </summary>
    internal sealed class LocalAvatarVisualProvider : IAvatarVisualDataProvider, IDisposable
    {
        private readonly IAvatarSystem _system;
        private readonly AvatarDataModel _avatarDataModel;
        private MultiplayerAvatarsData _data;

        public event Action<MultiplayerAvatarsData> visualDataDidChangeEvent;

        public MultiplayerAvatarsData avatarsData => _data;

        private LocalAvatarVisualProvider(IAvatarSystem system, AvatarDataModel avatarDataModel, MultiplayerAvatarsData data)
        {
            _system = system;
            _avatarDataModel = avatarDataModel;
            _data = data;
        }

        internal static async Task<LocalAvatarVisualProvider> CreateAsync(IAvatarSystem system, AvatarDataModel avatarDataModel)
        {
            var provider = new LocalAvatarVisualProvider(system, avatarDataModel, await FetchAsync(system));

            system.avatarDidChangeEvent += provider.HandleSystemAvatarDidChange;
            if (avatarDataModel != null)
                avatarDataModel.didChangeAvatarDataEvent += provider.HandleAvatarDataChanged;
            else
                Plugin.Log.Warn("AVBODY no AvatarDataModel: live avatar edits will not update the body");

            return provider;
        }

        private static async Task<MultiplayerAvatarsData> FetchAsync(IAvatarSystem system)
        {
            // The system hands back one MultiplayerAvatarData; the visual provider contract wants
            // the plural wrapper the network layer uses, so wrap it. BeatAvatar picks its own entry
            // back out by comparing avatarTypeIdentifierHash.
            MultiplayerAvatarData data = await system.GetMultiplayerAvatarsData();
            return Wrap(system, data);
        }

        private static MultiplayerAvatarsData Wrap(IAvatarSystem system, MultiplayerAvatarData data)
        {
            return new MultiplayerAvatarsData(
                new List<MultiplayerAvatarData> { data },
                system.supportedOptionalAvatarDataTypes ?? Enumerable.Empty<uint>());
        }

        /// <summary>
        /// The live path. The new AvatarData arrives with the event, so this needs no round trip
        /// back through the system -- and it works even before the edit has been saved to disk.
        /// </summary>
        private void HandleAvatarDataChanged(AvatarData avatarData)
        {
            try
            {
                if (avatarData == null) return;

                _data = Wrap(_system, avatarData.CreateMultiplayerAvatarsData());
                Plugin.Log.Info("AVBODY avatar edited, updating body:"
                    + " headTop=" + avatarData.headTopId
                    + " glasses=" + avatarData.glassesId
                    + " facialHair=" + avatarData.facialHairId
                    + " hands=" + avatarData.handsId
                    + " clothes=" + avatarData.clothesId);

                visualDataDidChangeEvent?.Invoke(_data);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error("AVBODY visual refresh failed: " + ex);
            }
        }

        private async void HandleSystemAvatarDidChange()
        {
            try
            {
                _data = await FetchAsync(_system);
                Plugin.Log.Info("AVBODY avatarDidChangeEvent fired (it never has before); updating body");
                visualDataDidChangeEvent?.Invoke(_data);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error("AVBODY visual refresh failed: " + ex);
            }
        }

        public void Dispose()
        {
            _system.avatarDidChangeEvent -= HandleSystemAvatarDidChange;
            if (_avatarDataModel != null)
                _avatarDataModel.didChangeAvatarDataEvent -= HandleAvatarDataChanged;
        }
    }
}
