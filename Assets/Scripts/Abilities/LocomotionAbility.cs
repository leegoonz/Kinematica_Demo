using Unity.Kinematica;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Assertions;

using SnapshotProvider = Unity.SnapshotDebugger.SnapshotProvider;

[RequireComponent(typeof(AbilityRunner))]
[RequireComponent(typeof(MovementController))]
public partial class LocomotionAbility : SnapshotProvider, Ability, AbilityAnimatorMove
{
    [Header("Prediction settings")]
    [Tooltip("Desired speed in meters per second for slow movement.")]
    [Range(0.0f, 10.0f)]
    public float desiredSpeedSlow = 3.9f;

    [Tooltip("Desired speed in meters per second for fast movement.")]
    [Range(0.0f, 10.0f)]
    public float desiredSpeedFast = 5.5f;

    [Tooltip("How fast or slow the target velocity is supposed to be reached.")]
    [Range(0.0f, 1.0f)]
    public float velocityPercentage = 1.0f;

    [Tooltip("How fast or slow the desired forward direction is supposed to be reached.")]
    [Range(0.0f, 1.0f)]
    public float forwardPercentage = 1.0f;

    [Tooltip("How much root motion distance should be corrected to match desired trajectory.")]
    [Range(0.0f, 1.0f)]
    public float correctTranslationPercentage = 0.0f;

    [Tooltip("How much root motion rotation should be corrected to match desired trajectory.")]
    [Range(0.0f, 1.0f)]
    public float correctRotationPercentage = 1.0f;

    [Tooltip("Minimum character move speed (m/s) before root motion correction is applied.")]
    [Range(0.0f, 10.0f)]
    public float correctMotionStartSpeed = 2.0f;

    [Tooltip("Character move speed (m/s) at which root motion correction is fully effective.")]
    [Range(0.0f, 10.0f)]
    public float correctMotionEndSpeed = 3.0f;


    Identifier<SelectorTask> locomotion;
    Identifier<Trajectory> trajectory;
    Identifier<TrajectoryHeuristicTask> trajectoryHeuristic;

    float3 cameraForward = Missing.forward;
    float3 movementDirection = Missing.forward;
    float3 forwardDirection = Missing.forward;
    float linearSpeed;

    float horizontal;
    float vertical;
    bool run;

    float desiredLinearSpeed => run ? desiredSpeedFast : desiredSpeedSlow;

    int previousFrameCount;

    bool isBraking = false;

    float3 rootVelocity = float3.zero;

    public override void OnEnable()
    {
        base.OnEnable();

        previousFrameCount = -1;

        var kinematica = GetComponent<Kinematica>();

       
        if (kinematica.Synthesizer.IsValid)
        {
            ref var synthesizer = ref kinematica.Synthesizer.Ref;

            rootVelocity = synthesizer.CurrentVelocity;
        }

    }

    public Ability OnUpdate(float deltaTime)
    {
        var kinematica = GetComponent<Kinematica>();

        ref var synthesizer = ref kinematica.Synthesizer.Ref;

        if (previousFrameCount != Time.frameCount - 1 || !synthesizer.IsIdentifierValid(locomotion))
        {
            var selector = synthesizer.Selector();

            {
                var sequence = selector.Condition().Sequence();

                sequence.Action().PushConstrained(
                    synthesizer.Query.Where(
                        Locomotion.Default).And(Idle.Default), 0.01f);

                sequence.Action().Timer();
            }

            {
                var action = selector.Action();

                this.trajectory = action.Trajectory();

                action.PushConstrained(
                    synthesizer.Query.Where(
                        Locomotion.Default).Except(Idle.Default),
                            this.trajectory);

                trajectoryHeuristic = action.GetByType<TrajectoryHeuristicTask>();


                action.GetByType<ReduceTask>().responsiveness = 0.45f;
            }

            locomotion = selector;
        }

        previousFrameCount = Time.frameCount;

        synthesizer.Tick(locomotion);

        ref var idle = ref synthesizer.GetByType<ConditionTask>(locomotion).Ref;

        float3 analogInput = Utility.GetAnalogInput(horizontal, vertical);

        idle.value = math.length(analogInput) <= 0.1f;

        if (idle)
        {
            linearSpeed = 0.0f;

            if (!isBraking && math.length(synthesizer.CurrentVelocity) < 0.4f)
            {
                isBraking = true;
            }
        }
        else
        {
            isBraking = false;

            movementDirection =
                Utility.GetDesiredForwardDirection(
                    analogInput, movementDirection, cameraForward);

            linearSpeed =
                math.length(analogInput) *
                    desiredLinearSpeed;

            forwardDirection = movementDirection;
        }

        // If character is braking, we set a strong deviation threshold on Trajectory Heuristic to be conservative (candidate would need to be a LOT better to be picked)
        // because then we want the character to pick a stop clip in the library and stick to it even if Kinematica can jump to a better clip (cost wise) in the middle 
        // of that stop animation. Indeed stop animations have very subtle foot steps (to reposition to idle stance) that would be squeezed by blend/jumping from clip to clip.
        // Moreover, playing a stop clip from start to end will make sure we will reach a valid transition point to idle.
        synthesizer.GetByType<TrajectoryHeuristicTask>(trajectoryHeuristic).Ref.threshold = isBraking ? 0.25f : 0.03f;

        var desiredVelocity = movementDirection * linearSpeed;

        var desiredRotation =
            Missing.forRotation(Missing.forward, forwardDirection);

        var trajectory =
            synthesizer.GetArray<AffineTransform>(
                this.trajectory);

        synthesizer.trajectory.Array.CopyTo(ref trajectory);

        var prediction = TrajectoryPrediction.Create(
            ref synthesizer, desiredVelocity, desiredRotation,
                trajectory, velocityPercentage, forwardPercentage, rootVelocity);

        var controller = GetComponent<MovementController>();

        Assert.IsTrue(controller != null);

        controller.Snapshot();

        var transform = prediction.Transform;

        var worldRootTransform = synthesizer.WorldRootTransform;

        float inverseSampleRate =
            Missing.recip(synthesizer.Binary.SampleRate);

        bool attemptTransition = true;

        while (prediction.Push(transform))
        {
            transform = prediction.Advance;

            controller.MoveTo(worldRootTransform.transform(transform.t));
            controller.Tick(inverseSampleRate);

            ref var closure = ref controller.current;

            if (closure.isColliding && attemptTransition)
            {
                float3 contactPoint = closure.colliderContactPoint;
                contactPoint.y = controller.Position.y;

                float3 contactNormal = closure.colliderContactNormal;
                quaternion q = math.mul(transform.q,
                    Missing.forRotation(Missing.zaxis(transform.q),
                        contactNormal));

                AffineTransform contactTransform = new AffineTransform(contactPoint, q);

                foreach (Ability ability in GetComponents(typeof(Ability)))
                {
                    if (ability.OnContact(ref synthesizer, contactTransform, deltaTime))
                    {
                        return ability;
                    }
                }

                attemptTransition = false;
            }
            else if (!closure.isGrounded)
            {
                foreach (Ability ability in GetComponents(typeof(Ability)))
                {
                    if (ability.OnDrop(ref synthesizer, deltaTime))
                    {
                        return ability;
                    }
                }
            }

            transform.t =
                worldRootTransform.inverseTransform(
                    controller.Position);

            prediction.Transform = transform;
        }

        controller.Rewind();

        return this;
    }

    public bool OnContact(ref MotionSynthesizer synthesizer, AffineTransform contactTransform, float deltaTime)
    {
        return false;
    }

    public bool OnDrop(ref MotionSynthesizer synthesizer, float deltaTime)
    {
        return false;
    }

    public void OnAnimatorMove()
    {
#if UNITY_EDITOR
        if (Unity.SnapshotDebugger.Debugger.instance.rewind)
        {
            return;
        }
#endif

        var kinematica = GetComponent<Kinematica>();
        if (kinematica.Synthesizer.IsValid)
        {
            ref MotionSynthesizer synthesizer = ref kinematica.Synthesizer.Ref;

            AffineTransform rootMotion = synthesizer.SteerRootMotion(trajectory, correctTranslationPercentage, correctRotationPercentage, correctMotionStartSpeed, correctMotionEndSpeed);
            AffineTransform rootTransform = AffineTransform.Create(transform.position, transform.rotation) * rootMotion;

            synthesizer.SetWorldTransform(AffineTransform.Create(rootTransform.t, rootTransform.q), true);

            if (synthesizer.deltaTime >= 0.0f)
            {
                rootVelocity = rootMotion.t / synthesizer.deltaTime;
            }
        }
    }
}
