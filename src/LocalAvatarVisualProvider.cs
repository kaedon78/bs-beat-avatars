using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BeatSaber.AvatarCore;
using BeatSaber.BeatAvatarAdapter;
using BeatSaber.BeatAvatarSDK;

namespace BeatAvatars
{
    /// <summary>
    /// Supplies the player's OWN saved avatar appearance -- the one edited in the game's avatar
    /// editor and stored in AvatarData.dat -- and pushes an update when they change it.
    ///
    /// The obvious hook, <see cref="IAvatarSystem.avatarDidChangeEvent"/>, is DEAD: its only
    /// raiser is protected and has no caller in any game assembly, so subscribing compiles, runs,
    /// and silently never fires.
    ///
    /// The signal that does fire is <see cref="AvatarDataModel.didChangeAvatarDataEvent"/>, which
    /// the game's own avatar editor listens to. Both are taken, so this keeps working if a later
    /// version starts raising the system-level one.
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
            {
                avatarDataModel.didChangeAvatarDataEvent += provider.HandleAvatarDataChanged;

                // Also the SAVE event: the change event comes off the avatarData setter and only
                // fires when the object is replaced, so an editor that mutates in place and saves
                // would never raise it. Between the two, either commit path is caught.
                avatarDataModel.didSaveAvatarDataEvent += provider.HandleAvatarDataSaved;
            }
            else
            {
                Plugin.Log.Warn("No AvatarDataModel: live avatar edits will not update the body.");
            }

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
        /// The live path: the new AvatarData arrives with the event, so this needs no round trip
        /// through the system and works before the edit is saved to disk.
        /// </summary>
        private void HandleAvatarDataChanged(AvatarData avatarData)
        {
            try
            {
                if (avatarData == null) return;

                _data = Wrap(_system, avatarData.CreateMultiplayerAvatarsData());
                visualDataDidChangeEvent?.Invoke(_data);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error("Visual refresh failed: " + ex);
            }
        }

        /// <summary>
        /// Reads the model back on save. Unlike the change event this carries no payload, so it
        /// goes the long way round through the system.
        /// </summary>
        private async void HandleAvatarDataSaved()
        {
            try
            {
                _data = await FetchAsync(_system);
                visualDataDidChangeEvent?.Invoke(_data);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error("Visual refresh failed: " + ex);
            }
        }

        private async void HandleSystemAvatarDidChange()
        {
            try
            {
                _data = await FetchAsync(_system);
                visualDataDidChangeEvent?.Invoke(_data);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error("Visual refresh failed: " + ex);
            }
        }

        public void Dispose()
        {
            _system.avatarDidChangeEvent -= HandleSystemAvatarDidChange;
            if (_avatarDataModel != null)
            {
                _avatarDataModel.didChangeAvatarDataEvent -= HandleAvatarDataChanged;
                _avatarDataModel.didSaveAvatarDataEvent -= HandleAvatarDataSaved;
            }
        }
    }
}
