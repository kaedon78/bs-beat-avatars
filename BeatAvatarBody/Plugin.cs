using System;
using IPA;
using UnityEngine;
using IPALogger = IPA.Logging.Logger;

namespace BeatAvatarBody
{
    /// <summary>
    /// First-person body presence built on the game's OWN avatar system, not on custom models.
    ///
    /// The whole mod is three small pieces bolted onto public API that already exists:
    ///   * <see cref="BeatSaber.AvatarCore.IAvatarSystem.InstantiateAvatar"/> spawns the same
    ///     BeatAvatar the game shows other players in multiplayer;
    ///   * <see cref="LocalPlayerPoseProvider"/> feeds it the local head and hands instead of a
    ///     network peer's;
    ///   * <see cref="AvatarLayers"/> keeps your own head out of your own eyes.
    ///
    /// There is deliberately no IK, no model loading and no calibration here. The BeatAvatar is
    /// a head, two floating hands and a body blob placed from a fixed neck offset -- that is the
    /// entire rig (BeatAvatarPoseController.UpdateTransforms), which is why this is a few hundred
    /// lines where CustomAvatars is a few thousand.
    /// </summary>
    [Plugin(RuntimeOptions.SingleStartInit)]
    public class Plugin
    {
        internal static IPALogger Log { get; private set; }

        [Init]
        public Plugin(IPALogger logger)
        {
            Log = logger;
            Log.Info("AVBODY Init");
        }

        [OnStart]
        public void OnStart()
        {
            var host = new GameObject("BeatAvatarBody");
            UnityEngine.Object.DontDestroyOnLoad(host);

            host.AddComponent<BeatAvatarBodyController>();
            host.AddComponent<UI.BeatAvatarBodyMenu>();

            // Opt-in diagnostic pass. It reads the layer table, every camera's culling mask, the
            // mirror's reflect mask and the spawned avatar's whole renderer hierarchy, then logs
            // them. That is the measurement the layer work depends on, and it cannot be made from
            // the DLLs: the avatar is an Addressable prefab.
            if (Environment.GetEnvironmentVariable("BSMU_AVATARPROBE") == "1")
            {
                Log.Info("AVBODY probe ENABLED");
                host.AddComponent<AvatarSystemProbe>();
            }
        }
    }
}
