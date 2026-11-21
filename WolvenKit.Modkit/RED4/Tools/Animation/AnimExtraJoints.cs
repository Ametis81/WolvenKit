using System;
using System.Collections.Generic;
using System.Linq;

using WolvenKit.Core.Interfaces;

using Quat = System.Numerics.Quaternion;
using Vec3 = System.Numerics.Vector3;

using JointsTranslationsAtTimes = System.Collections.Generic.Dictionary<ushort, System.Collections.Generic.Dictionary<float, System.Numerics.Vector3>>;
using JointsRotationsAtTimes = System.Collections.Generic.Dictionary<ushort, System.Collections.Generic.Dictionary<float, System.Numerics.Quaternion>>;
using JointsScalesAtTimes = System.Collections.Generic.Dictionary<ushort, System.Collections.Generic.Dictionary<float, System.Numerics.Vector3>>;

namespace WolvenKit.Modkit.RED4.Animation
{
    /// <summary>
    /// Extra joints (syncTrajectory...) are stored after the rig bones in the animation buffer.
    /// They have no rig bone and so no glTF node: their keys travel in the animation extras.
    /// Values stay in the glTF space the decoders produce, so export and import convert them
    /// the same way as the joint channels.
    /// </summary>
    internal static class AnimExtraJoints
    {
        private static List<ExtraJointKeySerializable>? Vec3Keys(Dictionary<float, Vec3>? keys) =>
            keys is { Count: > 0 }
                ? keys.OrderBy(k => k.Key).Select(k => new ExtraJointKeySerializable(k.Key, [k.Value.X, k.Value.Y, k.Value.Z])).ToList()
                : null;

        private static List<ExtraJointKeySerializable>? QuatKeys(Dictionary<float, Quat>? keys) =>
            keys is { Count: > 0 }
                ? keys.OrderBy(k => k.Key).Select(k => new ExtraJointKeySerializable(k.Key, [k.Value.X, k.Value.Y, k.Value.Z, k.Value.W])).ToList()
                : null;

        /// <summary>
        /// Collect the keys of the extra joints for the glTF extras. Null when there are none.
        /// </summary>
        internal static List<ExtraJointSerializable>? ToSerializable(in AnimationBufferData data)
        {
            if (data.NumExtraJoints == 0)
            {
                return null;
            }

            var result = new List<ExtraJointSerializable>();

            for (byte extraIdx = 0; extraIdx < data.NumExtraJoints; extraIdx++)
            {
                var jointIdx = (ushort)(data.JointsCountActual + extraIdx);

                var joint = new ExtraJointSerializable(
                    extraIdx,
                    Vec3Keys(data.Translations.GetValueOrDefault(jointIdx)),
                    QuatKeys(data.Rotations.GetValueOrDefault(jointIdx)),
                    Vec3Keys(data.Scales.GetValueOrDefault(jointIdx)),
                    Vec3Keys(data.ConstTranslations.GetValueOrDefault(jointIdx)),
                    QuatKeys(data.ConstRotations.GetValueOrDefault(jointIdx)),
                    Vec3Keys(data.ConstScales.GetValueOrDefault(jointIdx))
                );

                if (joint.Translations is null && joint.Rotations is null && joint.Scales is null &&
                    joint.ConstTranslations is null && joint.ConstRotations is null && joint.ConstScales is null)
                {
                    continue;
                }

                result.Add(joint);
            }

            return result.Count > 0 ? result : null;
        }

        private static Vec3 ToVec3(float[] v, string what) =>
            v is { Length: 3 }
                ? new Vec3(v[0], v[1], v[2])
                : throw new InvalidOperationException($"{what}: expected [x, y, z], got {v?.Length ?? 0} values");

        private static Quat ToQuat(float[] v, string what) =>
            v is { Length: 4 }
                ? Quat.Normalize(new Quat(v[0], v[1], v[2], v[3]))
                : throw new InvalidOperationException($"{what}: expected [x, y, z, w], got {v?.Length ?? 0} values");

        private static void Add<T>(Dictionary<ushort, Dictionary<float, T>> target, ushort jointIdx, List<ExtraJointKeySerializable>? keys, Func<float[], T> convert)
        {
            if (keys is not { Count: > 0 })
            {
                return;
            }

            if (!target.TryGetValue(jointIdx, out var atTimes))
            {
                atTimes = [];
                target[jointIdx] = atTimes;
            }

            foreach (var key in keys)
            {
                atTimes[key.Time] = convert(key.Value);
            }
        }

        /// <summary>
        /// Put the extra joints' keys from the glTF extras back after the rig bones.
        /// </summary>
        internal static void AddToKeyframes(
            List<ExtraJointSerializable>? extraJoints,
            ushort jointsCountActual,
            byte numExtraJoints,
            JointsTranslationsAtTimes translations,
            JointsTranslationsAtTimes constTranslations,
            JointsRotationsAtTimes rotations,
            JointsRotationsAtTimes constRotations,
            JointsScalesAtTimes scales,
            JointsScalesAtTimes constScales,
            string animName,
            ILoggerService logger)
        {
            if (extraJoints is null)
            {
                return;
            }

            foreach (var joint in extraJoints)
            {
                if (joint.ExtraIndex >= numExtraJoints)
                {
                    logger.Warning($"{animName}: extra joint {joint.ExtraIndex} is past numExtraJoints ({numExtraJoints}), skipping its keys.");
                    continue;
                }

                var jointIdx = (ushort)(jointsCountActual + joint.ExtraIndex);
                var what = $"{animName}: extra joint {joint.ExtraIndex}";

                Add(translations, jointIdx, joint.Translations, v => ToVec3(v, what));
                Add(rotations, jointIdx, joint.Rotations, v => ToQuat(v, what));
                Add(scales, jointIdx, joint.Scales, v => ToVec3(v, what));
                Add(constTranslations, jointIdx, joint.ConstTranslations, v => ToVec3(v, what));
                Add(constRotations, jointIdx, joint.ConstRotations, v => ToQuat(v, what));
                Add(constScales, jointIdx, joint.ConstScales, v => ToVec3(v, what));
            }
        }
    }
}
