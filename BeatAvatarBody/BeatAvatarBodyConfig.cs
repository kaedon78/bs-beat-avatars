using System;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace BeatAvatarBody
{
    /// <summary>
    /// UserData\BeatAvatarBody.json. Written with defaults on first run so there is something to
    /// edit, and read once at start-up.
    ///
    /// This exists mainly so the sizing knobs can be tuned without a rebuild. Whether the Beat
    /// Avatar's hands look right in first person is a judgement only someone in the headset can
    /// make, and the round trip through a rebuild and a redeploy for each guess is the expensive
    /// part of answering it.
    /// </summary>
    internal sealed class BeatAvatarBodyConfig
    {
        /// <summary>
        /// Uniform scale on the hand bones. The BeatAvatar's hands are drawn for a multiplayer
        /// avatar seen across a lobby, so at your own eye distance they read as oversized;
        /// 1.0 is the game's own size. Scaling the BONE and not the avatar root keeps tracking
        /// exact -- the bone's local position is written by the pose controller and is unaffected
        /// by the bone's own scale, so the hand shrinks around the controller rather than
        /// drifting away from it.
        /// </summary>
        public float handScale = 0.7f;

        /// <summary>
        /// Uniform scale on the head bone. Defaults to the same value as <see cref="handScale"/>:
        /// the head and hands are drawn to the same oversized multiplayer proportions, so a hand
        /// that needed shrinking implies a head that does too, and matching them keeps the avatar
        /// self-consistent for the cameras that can see it.
        /// </summary>
        public float headScale = 0.7f;

        /// <summary>Uniform scale on the body/clothes bone.</summary>
        public float bodyScale = 1.0f;

        /// <summary>Raises or lowers the head's visuals, in metres. Does not move the body.</summary>
        public float headVerticalOffset = 0f;

        /// <summary>Raises or lowers the torso's visuals, in metres.</summary>
        public float bodyVerticalOffset = 0f;

        /// <summary>Hide the head from the HMD camera. Off means you can see your own face.</summary>
        public bool hideHeadInFirstPerson = true;

        /// <summary>
        /// Follow the saber anchor rather than the raw controller pose, so the avatar's hands
        /// honour the player's controller position and rotation settings. False reverts to the raw
        /// tracked pose, which is what the multiplayer avatar uses.
        /// </summary>
        public bool useControllerOffsets = true;

        /// <summary>
        /// Where the hand sits relative to the saber anchor, in the ANCHOR's own local space, in
        /// metres. This is the grip offset: the anchor is the point the saber is mounted on, which
        /// is not where a real hand closes around the controller.
        ///
        /// Axes, in the anchor's frame:
        ///   z  along the handle -- NEGATIVE moves down the handle, away from the blade tip
        ///   y  up relative to the controller
        ///   x  sideways
        ///
        /// Applied identically to both hands in each hand's own space, so a change along y or z is
        /// symmetric without needing to be mirrored.
        /// </summary>
        public Offset handPositionOffset = new Offset { x = 0f, y = 0f, z = -0.05f };

        /// <summary>
        /// Extra rotation applied to each hand, in degrees, in the ANCHOR's local frame, on top of
        /// the player's own controller rotation settings.
        /// </summary>
        public Offset handRotationOffset = new Offset();

        /// <summary>
        /// Where the tuning preview's mirror CONTAINER sits, in player space, in metres. The
        /// apparent mirror surface is at half this distance -- the container is negatively scaled
        /// in z, so a bone at local z lands at (container.z - z), a reflection about z/2.
        /// </summary>
        public Offset previewPosition = new Offset { x = 0f, y = 0f, z = 1.6f };

        /// <summary>Plain x/y/z so the JSON stays readable; Vector3 serialises its derived properties too.</summary>
        internal sealed class Offset
        {
            public float x;
            public float y;
            public float z;

            internal Vector3 ToVector3() => new Vector3(x, y, z);

            internal static Vector3 ToVector3(Offset offset) =>
                offset == null ? Vector3.zero : offset.ToVector3();

            internal static Offset Copy(Offset offset) =>
                offset == null ? new Offset() : new Offset { x = offset.x, y = offset.y, z = offset.z };

            public override string ToString() => "(" + x + ", " + y + ", " + z + ")";
        }

        /// <summary>
        /// A value copy, used by the settings panel to remember what everything was when it opened
        /// so each slider can be put back individually.
        /// </summary>
        internal BeatAvatarBodyConfig Clone()
        {
            return new BeatAvatarBodyConfig
            {
                handScale = handScale,
                headScale = headScale,
                bodyScale = bodyScale,
                headVerticalOffset = headVerticalOffset,
                bodyVerticalOffset = bodyVerticalOffset,
                hideHeadInFirstPerson = hideHeadInFirstPerson,
                useControllerOffsets = useControllerOffsets,
                handPositionOffset = Offset.Copy(handPositionOffset),
                handRotationOffset = Offset.Copy(handRotationOffset),
                previewPosition = Offset.Copy(previewPosition),
            };
        }

        internal static BeatAvatarBodyConfig Load()
        {
            string path = Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", "UserData", "BeatAvatarBody.json"));

            try
            {
                if (File.Exists(path))
                {
                    var loaded = JsonConvert.DeserializeObject<BeatAvatarBodyConfig>(File.ReadAllText(path));
                    if (loaded != null) return loaded;
                }

                var defaults = new BeatAvatarBodyConfig();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(defaults, Formatting.Indented));
                return defaults;
            }
            catch (Exception ex)
            {
                Plugin.Log.Error("Config load failed, using defaults: " + ex);
                return new BeatAvatarBodyConfig();
            }
        }

        /// <summary>
        /// Writes the current values back. Called when the settings panel is dismissed rather than
        /// on every slider tick -- a slider drag raises a value change per frame, and rewriting the
        /// file at that rate is real disk traffic for no benefit.
        /// </summary>
        internal void Save()
        {
            try
            {
                string path = Path.GetFullPath(Path.Combine(
                    Application.dataPath, "..", "UserData", "BeatAvatarBody.json"));

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Plugin.Log.Error("Config save failed: " + ex);
            }
        }
    }
}
