using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Numerics;
using Content.Goobstation.Common.Movement;
using Content.Shared.ActionBlocker;
using Content.Shared.Bed.Sleep;
using Content.Shared.CCVar;
using Content.Shared.Friction;
using Content.Shared.Gravity;
using Content.Shared.Inventory;
using Content.Shared.Maps;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Events;
using Content.Shared.Tag;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Controllers;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
// Tile Movement Change
using Content.Shared.Interaction;
using Content.Shared.Physics;
using Content.Shared._vg.TileMovement;
using PullableComponent = Content.Shared.Movement.Pulling.Components.PullableComponent;

namespace Content.Shared.Movement.Systems;

/// <summary>
///     Handles player and NPC mob movement.
///     NPCs are handled server-side only.
/// </summary>
public abstract partial class SharedMoverController : VirtualController
{
    [Dependency] private   readonly IConfigurationManager _configManager = default!;
    [Dependency] protected readonly IGameTiming Timing = default!;
    [Dependency] private   readonly ITileDefinitionManager _tileDefinitionManager = default!;
    [Dependency] private   readonly ActionBlockerSystem _blocker = default!;
    [Dependency] private   readonly EntityLookupSystem _lookup = default!;
    [Dependency] private   readonly InventorySystem _inventory = default!;
    [Dependency] private   readonly MobStateSystem _mobState = default!;
    [Dependency] private   readonly SharedAudioSystem _audio = default!;
    [Dependency] private   readonly SharedContainerSystem _container = default!;
    [Dependency] private   readonly SharedMapSystem _mapSystem = default!;
    [Dependency] private   readonly SharedGravitySystem _gravity = default!;
    [Dependency] private   readonly SharedTransformSystem _transform = default!;
    [Dependency] private   readonly TagSystem _tags = default!;
    [Dependency] private   readonly SharedInteractionSystem _interaction = default!; // Tile Movement Change
    [Dependency] private   MuteFootstepSystem _muteFootstepSystem = default!; // Midnight: Holdout kit

    protected EntityQuery<CanMoveInAirComponent> CanMoveInAirQuery;
    protected EntityQuery<FootstepModifierComponent> FootstepModifierQuery;
    protected EntityQuery<InputMoverComponent> MoverQuery;
    protected EntityQuery<MapComponent> MapQuery;
    protected EntityQuery<MapGridComponent> MapGridQuery;
    protected EntityQuery<MobMoverComponent> MobMoverQuery;
    protected EntityQuery<MovementRelayTargetComponent> RelayTargetQuery;
    protected EntityQuery<MovementSpeedModifierComponent> ModifierQuery;
    protected EntityQuery<NoRotateOnMoveComponent> NoRotateQuery;
    protected EntityQuery<PhysicsComponent> PhysicsQuery;
    protected EntityQuery<RelayInputMoverComponent> RelayQuery;
    protected EntityQuery<PullableComponent> PullableQuery;
    protected EntityQuery<TransformComponent> XformQuery;

    private static readonly ProtoId<TagPrototype> FootstepSoundTag = "FootstepSound";

    protected EntityQuery<FixturesComponent> FixturesQuery; // Tile Movement Change
    protected EntityQuery<TileMovementComponent> TileMovementQuery; // Tile Movement Change

    private bool _relativeMovement;
    private float _minDamping;
    private float _airDamping;
    private float _offGridDamping;
    private TimeSpan CurrentTime => PhysicsSystem.EffectiveCurTime ?? Timing.CurTime; // Tile Movement Change

    /// <summary>
    /// Cache the mob movement calculation to re-use elsewhere.
    /// </summary>
    public Dictionary<EntityUid, bool> UsedMobMovement = new();

    public override void Initialize()
    {
        base.Initialize();

        MoverQuery = GetEntityQuery<InputMoverComponent>();
        MobMoverQuery = GetEntityQuery<MobMoverComponent>();
        ModifierQuery = GetEntityQuery<MovementSpeedModifierComponent>();
        RelayTargetQuery = GetEntityQuery<MovementRelayTargetComponent>();
        PhysicsQuery = GetEntityQuery<PhysicsComponent>();
        RelayQuery = GetEntityQuery<RelayInputMoverComponent>();
        PullableQuery = GetEntityQuery<PullableComponent>();
        XformQuery = GetEntityQuery<TransformComponent>();
        NoRotateQuery = GetEntityQuery<NoRotateOnMoveComponent>();
        CanMoveInAirQuery = GetEntityQuery<CanMoveInAirComponent>();
        FootstepModifierQuery = GetEntityQuery<FootstepModifierComponent>();
        MapGridQuery = GetEntityQuery<MapGridComponent>();
        FixturesQuery = GetEntityQuery<FixturesComponent>(); // Tile Movement Change
        TileMovementQuery = GetEntityQuery<TileMovementComponent>(); // Tile Movement Change
<<<<<<< HEAD
        _muteFootstepSystem = EntitySystem.Get<MuteFootstepSystem>(); // Midnight: Holdout kit
=======
        MapQuery = GetEntityQuery<MapComponent>();

        SubscribeLocalEvent<MovementSpeedModifierComponent, TileFrictionEvent>(OnTileFriction);
>>>>>>> goob/master

        InitializeInput();
        InitializeRelay();
        Subs.CVar(_configManager, CCVars.RelativeMovement, value => _relativeMovement = value, true);
        Subs.CVar(_configManager, CCVars.MinFriction, value => _minDamping = value, true);
        Subs.CVar(_configManager, CCVars.AirFriction, value => _airDamping = value, true);
        Subs.CVar(_configManager, CCVars.OffgridFriction, value => _offGridDamping = value, true);
        UpdatesBefore.Add(typeof(TileFrictionController));
    }

    public override void Shutdown()
    {
        base.Shutdown();
        ShutdownInput();
    }

    public override void UpdateAfterSolve(bool prediction, float frameTime)
    {
        base.UpdateAfterSolve(prediction, frameTime);
        UsedMobMovement.Clear();
    }

    /// <summary>
    ///     Movement while considering actionblockers, weightlessness, etc.
    /// </summary>
    protected void HandleMobMovement(
        Entity<InputMoverComponent> entity,
        float frameTime)
    {
        var uid = entity.Owner;
        var mover = entity.Comp;

        // If we're a relay then apply all of our data to the parent instead and go next.
        if (RelayQuery.TryComp(uid, out var relay))
        {
            if (!MoverQuery.TryComp(relay.RelayEntity, out var relayTargetMover))
                return;

            // Always lerp rotation so relay entities aren't cooked.
            LerpRotation(uid, mover, frameTime);
            var dirtied = false;

            if (relayTargetMover.RelativeEntity != mover.RelativeEntity)
            {
                relayTargetMover.RelativeEntity = mover.RelativeEntity;
                dirtied = true;
            }

            if (relayTargetMover.RelativeRotation != mover.RelativeRotation)
            {
                relayTargetMover.RelativeRotation = mover.RelativeRotation;
                dirtied = true;
            }

            if (relayTargetMover.TargetRelativeRotation != mover.TargetRelativeRotation)
            {
                relayTargetMover.TargetRelativeRotation = mover.TargetRelativeRotation;
                dirtied = true;
            }

            if (relayTargetMover.CanMove != mover.CanMove)
            {
                relayTargetMover.CanMove = mover.CanMove;
                dirtied = true;
            }

            if (dirtied)
            {
                Dirty(relay.RelayEntity, relayTargetMover);
            }

            return;
        }

        if (!XformQuery.TryComp(entity.Owner, out var xform))
            return;

        RelayTargetQuery.TryComp(uid, out var relayTarget);
        var relaySource = relayTarget?.Source;

        // If we're not the target of a relay then handle lerp data.
        if (relaySource == null)
        {
            // Update relative movement
            if (mover.LerpTarget < Timing.CurTime)
            {
                TryUpdateRelative(uid, mover, xform);
            }

            LerpRotation(uid, mover, frameTime);
        }

        // If we can't move then just use tile-friction / no movement handling.
        if (!mover.CanMove
            || !PhysicsQuery.TryComp(uid, out var physicsComponent)
            || PullableQuery.TryGetComponent(uid, out var pullable) && pullable.BeingPulled)
        {
            // Goobstation start
            var cantMoveEvent = new MoverControllerCantMoveEvent();
            RaiseLocalEvent(uid, ref cantMoveEvent);
            // Goobstation end
            UsedMobMovement[uid] = false;
            return;
        }

        // If the body is in air but isn't weightless then it can't move
        // TODO: MAKE ISWEIGHTLESS EVENT BASED
        var weightless = _gravity.IsWeightless(uid, physicsComponent, xform);
        var inAirHelpless = false;

        if (physicsComponent.BodyStatus != BodyStatus.OnGround && !CanMoveInAirQuery.HasComponent(uid))
        {
            if (!weightless)
            {
                UsedMobMovement[uid] = false;
                return;
            }
            inAirHelpless = true;
        }

        UsedMobMovement[uid] = true;

        var moveSpeedComponent = ModifierQuery.CompOrNull(uid);

        float friction;
        float accel;
        Vector2 wishDir;
        var velocity = physicsComponent.LinearVelocity;

        // Get current tile def for things like speed/friction mods
        ContentTileDefinition? tileDef = null;

        // Tile Movement Change
        // Try doing tile movement.
        // Temporairly nuked.
        //if (TileMovementQuery.TryComp(physicsUid, out var tileMovement))
        //{
        //    if (!weightless && !inAirHelpless)
        //    {
        //        var didTileMovement = HandleTileMovement(uid,
        //            physicsUid,
        //            tileMovement,
        //            physicsComponent,
        //            xform,
        //            mover,
        //            tileDef,
        //            relayTarget,
        //            frameTime);
        //        tileMovement.WasWeightlessLastTick = weightless;
        //        if(didTileMovement)
        //        {
        //            return;
        //        }
        //    }
        //    else
        //    {
        //        tileMovement.WasWeightlessLastTick = weightless;
        //        tileMovement.SlideActive = false;
        //    }
        //}

        var touching = false;
        // Whether we use tilefriction or not
        if (weightless || inAirHelpless)
        {
            // Find the speed we should be moving at and make sure we're not trying to move faster than that
            var walkSpeed = moveSpeedComponent?.WeightlessWalkSpeed ?? MovementSpeedModifierComponent.DefaultBaseWalkSpeed;
            var sprintSpeed = moveSpeedComponent?.WeightlessSprintSpeed ?? MovementSpeedModifierComponent.DefaultBaseSprintSpeed;

            wishDir = AssertValidWish(mover, walkSpeed, sprintSpeed);

            var ev = new CanWeightlessMoveEvent(uid);
            RaiseLocalEvent(uid, ref ev, true);

            touching = ev.CanMove || xform.GridUid != null || MapGridQuery.HasComp(xform.GridUid);

            // If we're not on a grid, and not able to move in space check if we're close enough to a grid to touch.
            if (!touching && MobMoverQuery.TryComp(uid, out var mobMover))
                touching |= IsAroundCollider(PhysicsSystem, xform, mobMover, uid, physicsComponent);

            // If we're touching then use the weightless values
            if (touching)
            {
                touching = true;
                if (wishDir != Vector2.Zero)
                    friction = moveSpeedComponent?.WeightlessFriction ?? _airDamping;
                else
                    friction = moveSpeedComponent?.WeightlessFrictionNoInput ?? _airDamping;
            }
            // Otherwise use the off-grid values.
            else
            {
                friction = moveSpeedComponent?.OffGridFriction ?? _offGridDamping;
            }

            accel = moveSpeedComponent?.WeightlessAcceleration ?? MovementSpeedModifierComponent.DefaultWeightlessAcceleration;
        }
        else
        {
            if (MapGridQuery.TryComp(xform.GridUid, out var gridComp)
                && _mapSystem.TryGetTileRef(xform.GridUid.Value, gridComp, xform.Coordinates, out var tile)
                && physicsComponent.BodyStatus == BodyStatus.OnGround)
                tileDef = (ContentTileDefinition) _tileDefinitionManager[tile.Tile.TypeId];

            var walkSpeed = moveSpeedComponent?.CurrentWalkSpeed ?? MovementSpeedModifierComponent.DefaultBaseWalkSpeed;
            var sprintSpeed = moveSpeedComponent?.CurrentSprintSpeed ?? MovementSpeedModifierComponent.DefaultBaseSprintSpeed;

            wishDir = AssertValidWish(mover, walkSpeed, sprintSpeed);

            if (wishDir != Vector2.Zero)
            {
                friction = moveSpeedComponent?.Friction ?? MovementSpeedModifierComponent.DefaultFriction;
                friction *= tileDef?.MobFriction ?? tileDef?.Friction ?? 1f;
            }
            else
            {
                friction = moveSpeedComponent?.FrictionNoInput ?? MovementSpeedModifierComponent.DefaultFrictionNoInput;
                friction *= tileDef?.Friction ?? 1f;
            }

            accel = moveSpeedComponent?.Acceleration ?? MovementSpeedModifierComponent.DefaultAcceleration;
            accel *= tileDef?.MobAcceleration ?? 1f;
        }

        // Goobstation start
        var getTileEvent = new MoverControllerGetTileEvent(tileDef);
        RaiseLocalEvent(uid, ref getTileEvent);
        // Goobstation end

        // This way friction never exceeds acceleration when you're trying to move.
        // If you want to slow down an entity with "friction" you shouldn't be using this system.
        if (wishDir != Vector2.Zero)
            friction = Math.Min(friction, accel);
        friction = Math.Max(friction, _minDamping);
        var minimumFrictionSpeed = moveSpeedComponent?.MinimumFrictionSpeed ?? MovementSpeedModifierComponent.DefaultMinimumFrictionSpeed;
        Friction(minimumFrictionSpeed, frameTime, friction, ref velocity);

        if (!weightless || touching)
            Accelerate(ref velocity, in wishDir, accel, frameTime);

        SetWishDir((uid, mover), wishDir);

        /*
         * SNAKING!!! >-( 0 ================>
         * Snaking is a feature where you can move faster by strafing in a direction perpendicular to the
         * direction you intend to move while still holding the movement key for the direction you're trying to move.
         * Snaking only works if acceleration exceeds friction, and it's effectiveness scales as acceleration continues
         * to exceed friction.
         * Snaking works because friction is applied first in the direction of our current velocity, while acceleration
         * is applied after in our "Wish Direction" and is capped by the dot of our wish direction and current direction.
         * This means when you change direction, you're technically able to accelerate more than what the velocity cap
         * allows, but friction normally eats up the extra movement you gain.
         * By strafing as stated above you can increase your speed by about 1.4 (square root of 2).
         * This only works if friction is low enough so be sure that anytime you are letting a mob move in a low friction
         * environment you take into account the fact they can snake! Also be sure to lower acceleration as well to
         * prevent jerky movement!
         */
        PhysicsSystem.SetLinearVelocity(uid, velocity, body: physicsComponent);

        // Ensures that players do not spiiiiiiin
        PhysicsSystem.SetAngularVelocity(uid, 0, body: physicsComponent);

        // Handle footsteps at the end
        if (wishDir != Vector2.Zero)
        {
            if (!NoRotateQuery.HasComponent(uid))
            {
                // TODO apparently this results in a duplicate move event because "This should have its event run during
                // island solver"??. So maybe SetRotation needs an argument to avoid raising an event?
                var worldRot = _transform.GetWorldRotation(xform);

                _transform.SetLocalRotation(uid, xform.LocalRotation + wishDir.ToWorldAngle() - worldRot, xform);
            }

            if (!weightless && MobMoverQuery.TryGetComponent(uid, out var mobMover) &&
                TryGetSound(weightless, uid, mover, mobMover, xform, out var sound, tileDef: tileDef))
            {
                var soundModifier = mover.Sprinting ? 3.5f : 1.5f;

                var audioParams = sound.Params
                    .WithVolume(sound.Params.Volume + soundModifier)
                    .WithVariation(sound.Params.Variation ?? mobMover.FootstepVariation);

                // If we're a relay target then predict the sound for all relays.
                if (relaySource != null)
                {
                    _audio.PlayPredicted(sound, uid, relaySource.Value, audioParams);
                }
                else
                {
                    _audio.PlayPredicted(sound, uid, uid, audioParams);
                }
            }
        }
    }

    public Vector2 GetWishDir(Entity<InputMoverComponent?> mover)
    {
        if (!MoverQuery.Resolve(mover.Owner, ref mover.Comp, false))
            return Vector2.Zero;

        return mover.Comp.WishDir;
    }

    public void SetWishDir(Entity<InputMoverComponent> mover, Vector2 wishDir)
    {
        if (mover.Comp.WishDir.Equals(wishDir))
            return;

        mover.Comp.WishDir = wishDir;
        Dirty(mover);
    }

    public void LerpRotation(EntityUid uid, InputMoverComponent mover, float frameTime)
    {
        var angleDiff = Angle.ShortestDistance(mover.RelativeRotation, mover.TargetRelativeRotation);

        // if we've just traversed then lerp to our target rotation.
        if (!angleDiff.EqualsApprox(Angle.Zero, 0.001))
        {
            var adjustment = angleDiff * 5f * frameTime;
            var minAdjustment = 0.01 * frameTime;

            if (angleDiff < 0)
            {
                adjustment = Math.Min(adjustment, -minAdjustment);
                adjustment = Math.Clamp(adjustment, angleDiff, -angleDiff);
            }
            else
            {
                adjustment = Math.Max(adjustment, minAdjustment);
                adjustment = Math.Clamp(adjustment, -angleDiff, angleDiff);
            }

            mover.RelativeRotation = (mover.RelativeRotation + adjustment).FlipPositive();
            Dirty(uid, mover);
        }
        else if (!angleDiff.Equals(Angle.Zero))
        {
            mover.RelativeRotation = mover.TargetRelativeRotation.FlipPositive();
            Dirty(uid, mover);
        }
    }

    public void Friction(float minimumFrictionSpeed, float frameTime, float friction, ref Vector2 velocity)
    {
        var speed = velocity.Length();

        if (speed < minimumFrictionSpeed)
            return;

        // This equation is lifted from the Physics Island solver.
        // We re-use it here because Kinematic Controllers can't/shouldn't use the Physics Friction
        velocity *= Math.Clamp(1.0f - frameTime * friction, 0.0f, 1.0f);

    }

    public void Friction(float minimumFrictionSpeed, float frameTime, float friction, ref float velocity)
    {
        if (velocity < minimumFrictionSpeed)
            return;

        // This equation is lifted from the Physics Island solver.
        // We re-use it here because Kinematic Controllers can't/shouldn't use the Physics Friction
        velocity *= Math.Clamp(1.0f - frameTime * friction, 0.0f, 1.0f);

    }

    /// <summary>
    /// Adjusts the current velocity to the target velocity based on the specified acceleration.
    /// </summary>
    public static void Accelerate(ref Vector2 currentVelocity, in Vector2 velocity, float accel, float frameTime)
    {
        var wishDir = velocity != Vector2.Zero ? velocity.Normalized() : Vector2.Zero;
        var wishSpeed = velocity.Length();

        var currentSpeed = Vector2.Dot(currentVelocity, wishDir);
        var addSpeed = wishSpeed - currentSpeed;

        if (addSpeed <= 0f)
            return;

        var accelSpeed = accel * frameTime * wishSpeed;
        accelSpeed = MathF.Min(accelSpeed, addSpeed);

        currentVelocity += wishDir * accelSpeed;
    }

    public bool UseMobMovement(EntityUid uid)
    {
        return UsedMobMovement.TryGetValue(uid, out var used) && used;
    }

    /// <summary>
    ///     Used for weightlessness to determine if we are near a wall.
    /// </summary>
    private bool IsAroundCollider(SharedPhysicsSystem broadPhaseSystem, TransformComponent transform, MobMoverComponent mover, EntityUid physicsUid, PhysicsComponent collider)
    {
        var enlargedAABB = _lookup.GetWorldAABB(physicsUid, transform).Enlarged(mover.GrabRangeVV);

        foreach (var otherCollider in broadPhaseSystem.GetCollidingEntities(transform.MapID, enlargedAABB))
        {
            if (otherCollider == collider)
                continue; // Don't try to push off of yourself!

            // Only allow pushing off of anchored things that have collision.
            if (otherCollider.BodyType != BodyType.Static ||
                !otherCollider.CanCollide ||
                ((collider.CollisionMask & otherCollider.CollisionLayer) == 0 &&
                (otherCollider.CollisionMask & collider.CollisionLayer) == 0) ||
                (TryComp(otherCollider.Owner, out PullableComponent? pullable) && pullable.BeingPulled))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    protected abstract bool CanSound();

    private bool TryGetSound(
        bool weightless,
        EntityUid uid,
        InputMoverComponent mover,
        MobMoverComponent mobMover,
        TransformComponent xform,
        [NotNullWhen(true)] out SoundSpecifier? sound,
        ContentTileDefinition? tileDef = null)
    {
        sound = null;

        if (!CanSound() || !_tags.HasTag(uid, FootstepSoundTag))
            return false;

        // Midnight: Check for shoes that mute walking footsteps
        if (_muteFootstepSystem.ShouldMute(uid, mover))
        {
            return false;
        }
        
        var coordinates = xform.Coordinates;
        var distanceNeeded = mover.Sprinting
            ? mobMover.StepSoundMoveDistanceRunning
            : mobMover.StepSoundMoveDistanceWalking;

        // Handle footsteps.
        if (!weightless)
        {
            // Can happen when teleporting between grids.
            if (!coordinates.TryDistance(EntityManager, mobMover.LastPosition, out var distance) ||
                distance > distanceNeeded)
            {
                mobMover.StepSoundDistance = distanceNeeded;
            }
            else
            {
                mobMover.StepSoundDistance += distance;
            }
        }
        else
        {
            // In space no one can hear you squeak
            return false;
        }

        mobMover.LastPosition = coordinates;

        if (mobMover.StepSoundDistance < distanceNeeded)
            return false;

        mobMover.StepSoundDistance -= distanceNeeded;

        if (FootstepModifierQuery.TryComp(uid, out var moverModifier))
        {
            sound = moverModifier.FootstepSoundCollection;
            return sound != null;
        }

        if (_inventory.TryGetSlotEntity(uid, "shoes", out var shoes) &&
            FootstepModifierQuery.TryComp(shoes, out var modifier))
        {
            sound = modifier.FootstepSoundCollection;
            return sound != null;
        }

        return TryGetFootstepSound(uid, xform, shoes != null, out sound, tileDef: tileDef);
    }

    private bool TryGetFootstepSound(
        EntityUid uid,
        TransformComponent xform,
        bool haveShoes,
        [NotNullWhen(true)] out SoundSpecifier? sound,
        ContentTileDefinition? tileDef = null)
    {
        sound = null;

        // Fallback to the map?
        if (!MapGridQuery.TryComp(xform.GridUid, out var grid))
        {
            if (FootstepModifierQuery.TryComp(xform.MapUid, out var modifier))
            {
                sound = modifier.FootstepSoundCollection;
            }

            return sound != null;
        }

        var position = grid.LocalToTile(xform.Coordinates);
        var soundEv = new GetFootstepSoundEvent(uid);

        // If the coordinates have a FootstepModifier component
        // i.e. component that emit sound on footsteps emit that sound
        var anchored = grid.GetAnchoredEntitiesEnumerator(position);

        while (anchored.MoveNext(out var maybeFootstep))
        {
            RaiseLocalEvent(maybeFootstep.Value, ref soundEv);

            if (soundEv.Sound != null)
            {
                sound = soundEv.Sound;
                return true;
            }

            if (FootstepModifierQuery.TryComp(maybeFootstep, out var footstep))
            {
                sound = footstep.FootstepSoundCollection;
                return sound != null;
            }
        }

        // Walking on a tile.
        // Tile def might have been passed in already from previous methods, so use that
        // if we have it
        if (tileDef == null && grid.TryGetTileRef(position, out var tileRef))
        {
            tileDef = (ContentTileDefinition) _tileDefinitionManager[tileRef.Tile.TypeId];
        }

        if (tileDef == null)
            return false;

        sound = haveShoes ? tileDef.FootstepSounds : tileDef.BarestepSounds;
        return sound != null;
    }

    private Vector2 AssertValidWish(InputMoverComponent mover, float walkSpeed, float sprintSpeed)
    {
        var (walkDir, sprintDir) = GetVelocityInput(mover);

        var total = walkDir * walkSpeed + sprintDir * sprintSpeed;

        var parentRotation = GetParentGridAngle(mover);
        var wishDir = _relativeMovement ? parentRotation.RotateVec(total) : total;

        DebugTools.Assert(MathHelper.CloseToPercent(total.Length(), wishDir.Length()));

        return wishDir;
    }

    private void OnTileFriction(Entity<MovementSpeedModifierComponent> ent, ref TileFrictionEvent args)
    {
        if (!TryComp<PhysicsComponent>(ent, out var physicsComponent) || !XformQuery.TryComp(ent, out var xform))
            return;

        // TODO: Make IsWeightless event based!!!
        if (physicsComponent.BodyStatus != BodyStatus.OnGround || _gravity.IsWeightless(ent, physicsComponent, xform))
            args.Modifier *= ent.Comp.BaseWeightlessFriction;
        else
            args.Modifier *= ent.Comp.BaseFriction;
    }

    // Tile Movement Functions

    /// /vg/station Tile Movement!
    /// Uses a physics-based implementation, resulting in fluid tile movement that mixes the responsiveness of
    /// pixel movement and the rigidity of tiles. Works surprisingly well.
    /// Note: the code is intentionally separated here from everything else to make it easier to port and
    /// to reduce the risk of merge conflicts.
    /// However, I would also NOT recommend porting it right now unless you're okay with continually updating it.
    /// For one, a shapecast-based implementation rather than a true physics implementation is in the cards for
    /// the future. For another, it's not terribly clean and is not integrated too well into existing movement code.

    /// <summary>
    /// Runs one tick of tile-based movement on the given inputs.
    /// </summary>
    /// <param name="uid">UID of the entity doing the move.</param>
    /// <param name="physicsUid">UID of the physics entity doing the move. Usually the same as uid.</param>
    /// <param name="tileMovement">TileMovementComponent on the entity doing the move.</param>
    /// <param name="physicsComponent">PhysicsComponent on the entity doing the move.</param>
    /// <param name="targetTransform">TransformComponent on the entity doing the move.</param>
    /// <param name="inputMover">InputMoverComponent on the entity doing the move.</param>
    /// <param name="tileDef">ContentTileDefinition of the tile underneath the entity doing the move, if there is one.</param>
    /// <param name="relayTarget">MovementRelayTargetComponent on the relay target, if any.</param>
    /// <param name="frameTime">Time in seconds since the last tick of the physics system.</param>
    /// <returns></returns>
    public bool HandleTileMovement(
        EntityUid uid,
        EntityUid physicsUid,
        TileMovementComponent tileMovement,
        PhysicsComponent physicsComponent,
        TransformComponent targetTransform,
        InputMoverComponent inputMover,
        ContentTileDefinition? tileDef,
        MovementRelayTargetComponent? relayTarget,
        float frameTime
    )
    {
        // For smoothness' sake, if we just arrived on a grid after pixel moving in space then initiate a slide
        // towards the center of the tile we're on and continue. It feels much nicer this way.
        if (tileMovement.WasWeightlessLastTick)
        {
            InitializeSlideToCenter(physicsUid, tileMovement);
            UpdateSlide(physicsUid, physicsUid, tileMovement, inputMover);
        }
        // If we're not moving, apply friction to existing velocity and then continue.
        else if (StripWalk(inputMover.HeldMoveButtons) == MoveButtons.None && !tileMovement.SlideActive)
        {
            var movementVelocity = physicsComponent.LinearVelocity;

            var movementSpeedComponent = ModifierQuery.CompOrNull(uid);
            var friction = GetEntityFriction(inputMover, movementSpeedComponent, tileDef);
            var minimumFrictionSpeed = movementSpeedComponent?.MinimumFrictionSpeed ??
                MovementSpeedModifierComponent.DefaultMinimumFrictionSpeed;
            Friction(minimumFrictionSpeed, frameTime, friction, ref movementVelocity);

            PhysicsSystem.SetLinearVelocity(physicsUid, movementVelocity, body: physicsComponent);
            PhysicsSystem.SetAngularVelocity(physicsUid, 0, body: physicsComponent);
        }
        // Otherwise, handle typical tile movement.
        else
        {
            // Play step sound.
            bool shouldMute = _muteFootstepSystem.ShouldMute(uid, inputMover);
            
            if (!shouldMute && 
                MobMoverQuery.TryGetComponent(uid, out var mobMover) &&
                TryGetSound(false, uid, inputMover, mobMover, targetTransform, out var sound, tileDef: tileDef))
            {
                var soundModifier = inputMover.Sprinting ? 3.5f : 1.5f;
                var volume = sound.Params.Volume + soundModifier;

                var audioParams = sound.Params
                    .WithVolume(volume)
                    .WithVariation(sound.Params.Variation ?? mobMover.FootstepVariation);

                // If we're a relay target then predict the sound for all relays.
                if (relayTarget != null)
                {
                    _audio.PlayPredicted(sound, uid, relayTarget.Source, audioParams);
                }
                else
                {
                    _audio.PlayPredicted(sound, uid, uid, audioParams);
                }
            }

            // If we're sliding...
            if (tileMovement.SlideActive)
            {
                var movementSpeed = GetEntityMoveSpeed(uid, inputMover.Sprinting);

                // Check whether we should end the slide.
                if (CheckForSlideEnd(
                    StripWalk(inputMover.HeldMoveButtons),
                    targetTransform,
                    tileMovement,
                    movementSpeed))
                {
                    EndSlide(uid, tileMovement);

                    // After ending the slide, check for immediately starting a new slide.
                    if (StripWalk(inputMover.HeldMoveButtons) != MoveButtons.None)
                    {
                        InitializeSlide(physicsUid, tileMovement, inputMover);
                        UpdateSlide(physicsUid, physicsUid, tileMovement, inputMover);
                        tileMovement.FailureSlideActive = false;
                    }
                    // Otherwise if we failed to reach the destination, begin a "failure slide" back to the
                    // original position.
                    else if(!tileMovement.FailureSlideActive && !targetTransform.LocalPosition.EqualsApprox(tileMovement.Destination, 0.04))
                    {
                        InitializeSlideToTarget(physicsUid, tileMovement, targetTransform.LocalPosition, MoveButtons.None);
                        UpdateSlide(physicsUid, physicsUid, tileMovement, inputMover);
                        tileMovement.FailureSlideActive = true;
                    }
                    // If we reached proper destination or have already done a "failure slide", snap to tile forcefully.
                    else
                    {
                        ForceSnapToTile(uid, inputMover);
                        tileMovement.FailureSlideActive = false;
                    }
                }
                // Special case: tile movement takes us between two fully adjacent grids seamlessly.
                // Since we perform tile movement in local coordinates, stop and start the movement
                // again to realign to new grid.
                // Improvement suggestion: this is mostly smooth but there is a very tiny bit of
                // jitter. Instead of being lazy and stopping/starting a new movement, it should
                // convert the origin into the coordinate system with the new grid as the parent.
                else if (tileMovement.Origin.EntityId != targetTransform.ParentUid)
                {
                    var previousButtons = tileMovement.CurrentSlideMoveButtons;
                    var previousInitialKeyDownTime = tileMovement.MovementKeyInitialDownTime;
                    InitializeSlideToCenter(physicsUid, tileMovement);
                    tileMovement.CurrentSlideMoveButtons = previousButtons;
                    tileMovement.MovementKeyInitialDownTime = previousInitialKeyDownTime;
                    UpdateSlide(physicsUid, physicsUid, tileMovement, inputMover);
                }
                // Otherwise, continue slide.
                else
                {
                    UpdateSlide(physicsUid, physicsUid, tileMovement, inputMover);
                }
            }
            // If we're not sliding, start slide.
            else
            {
                InitializeSlide(physicsUid, tileMovement, inputMover);
                UpdateSlide(physicsUid, physicsUid, tileMovement, inputMover);
            }

            // Set WorldRotation so that our character is facing the way we're walking.
            if (!NoRotateQuery.HasComponent(uid) && !tileMovement.FailureSlideActive)
            {
                if (tileMovement.SlideActive && TryComp(
                    inputMover.RelativeEntity,
                    out TransformComponent? parentTransform))
                {
                    var delta = tileMovement.Destination - tileMovement.Origin.Position;
                    var worldRot = _transform.GetWorldRotation(parentTransform).RotateVec(delta).ToWorldAngle();
                    _transform.SetWorldRotation(targetTransform, worldRot);
                }
            }
        }

        tileMovement.LastTickLocalCoordinates = targetTransform.LocalPosition;
        Dirty(uid, tileMovement);
        return true;
    }

    private bool CheckForSlideEnd(
        MoveButtons pressedButtons,
        TransformComponent transform,
        TileMovementComponent tileMovement,
        float movementSpeed
    )
    {
        // minPressedTime will be 1.05x the time it should take for you to go from 1 tile to another. Need to
        // account for diagonals being sqrt(2) length as well. Max of 10 seconds just in case.
        var distanceToDestination = (tileMovement.Destination - tileMovement.Origin.Position).Length();
        var minPressedTime = Math.Min((1.05f / movementSpeed) * distanceToDestination, 20);

        // We need to stop the move once we are close enough. This isn't perfect, since it technically ends the move
        // 1 tick early in some cases. This is because there's a fundamental issue where because this is a physics-based
        // tile movement system, we sometimes find scenarios where on each tick of the physics system, the player is moved
        // back and forth across the destination in a loop. Thus, the tolerance needs to be set overly high so that it
        // reaches the distance one the physics body can move in a single tick.
        float destinationTolerance = movementSpeed / 100f;

        var reachedDestination =
            transform.LocalPosition.EqualsApprox(tileMovement.Destination, destinationTolerance);
        var stoppedPressing = pressedButtons != tileMovement.CurrentSlideMoveButtons;
        var minDurationPassed = CurrentTime - tileMovement.MovementKeyInitialDownTime >= TimeSpan.FromSeconds(minPressedTime);
        var noProgress = tileMovement.LastTickLocalCoordinates != null && transform.LocalPosition.EqualsApprox(tileMovement.LastTickLocalCoordinates.Value, destinationTolerance/3);
        var hardDurationLimitPassed = CurrentTime - tileMovement.MovementKeyInitialDownTime >= TimeSpan.FromSeconds(minPressedTime) * 3;
        return reachedDestination || (stoppedPressing && (minDurationPassed || noProgress)) || hardDurationLimitPassed;
    }


    /// <summary>
    /// Initializes a slide, setting destination and other variables needed to start a slide to the given
    /// position (which is a local coordinate relative to the parent of the given uid).
    /// </summary>
    /// <param name="uid">UID of the entity that will be performing the slide.</param>
    /// <param name="tileMovement">TileMovementComponent on the entity represented by UID.</param>
    /// <param name="localPositionTarget">Target of the slide coordinates local to the parent entity of uid.</param>
    /// <param name="heldMoveButtons">Buttons used to initiate this slide.</param>
    private void InitializeSlideToTarget(
        EntityUid uid,
        TileMovementComponent tileMovement,
        Vector2 localPositionTarget,
        MoveButtons heldMoveButtons)
    {
        var transform = Transform(uid);
        var localPosition = transform.LocalPosition;

        tileMovement.SlideActive = true;
        tileMovement.Origin = new EntityCoordinates(transform.ParentUid, localPosition);
        tileMovement.Destination = SnapCoordinatesToTile(localPositionTarget);
        tileMovement.MovementKeyInitialDownTime = CurrentTime;
        tileMovement.CurrentSlideMoveButtons = heldMoveButtons;
    }

    /// <summary>
    /// Initializes a slide, setting destination and other variables needed to start a slide to the center of the
    /// tile the entity is currently on.
    /// </summary>
    /// <param name="uid">UID of the entity that will be performing the slide.</param>
    /// <param name="tileMovement">TileMovementComponent on the entity represented by UID.</param>
    private void InitializeSlideToCenter(EntityUid uid, TileMovementComponent tileMovement)
    {
        var localPosition = Transform(uid).LocalPosition;
        InitializeSlideToTarget(uid, tileMovement, SnapCoordinatesToTile(localPosition), MoveButtons.None);
    }

    /// <summary>
    /// Initializes a slide, setting destination and other variables needed to move in the direction currently given by
    /// the InputMoverComponent.
    /// </summary>
    /// <param name="uid">UID of the entity that will be performing the slide.</param>
    /// <param name="tileMovement">TileMovementComponent on the entity represented by UID.</param>
    /// <param name="inputMover">InputMoverComponent on the entity represented by UID.</param>
    private void InitializeSlide(EntityUid uid, TileMovementComponent tileMovement, InputMoverComponent inputMover)
    {
        var transform = Transform(uid);
        var localPosition = transform.LocalPosition;
        var offset = DirVecForButtons(inputMover.HeldMoveButtons);
        offset = inputMover.TargetRelativeRotation.RotateVec(offset);

        InitializeSlideToTarget(uid, tileMovement, localPosition + offset, StripWalk(inputMover.HeldMoveButtons));
    }

    /// <summary>
    /// Updates the velocity of the current physics-based tile movement slide on the given entity.
    /// </summary>
    /// <param name="uid">UID of the entity being moved.</param>
    /// <param name="physicsUid">UID of the entity with the physics body being moved. Usually the same as uid.</param>
    /// <param name="tileMovement">TileMovementComponent on the entity that's being moved.</param>
    /// <param name="inputMover">InputMoverComponent of the person controlling the move.</param>
    private void UpdateSlide(
        EntityUid uid,
        EntityUid physicsUid,
        TileMovementComponent tileMovement,
        InputMoverComponent inputMover
    )
    {
        var targetTransform = Transform(uid);

        if (PhysicsQuery.TryComp(physicsUid, out var physicsComponent))
        {
            // Gather some components and values.
            var moveSpeedComponent = ModifierQuery.CompOrNull(uid);
            var parentRotation = Angle.Zero;
            if (XformQuery.TryGetComponent(targetTransform.GridUid, out var relativeTransform))
            {
                parentRotation = _transform.GetWorldRotation(relativeTransform);
            }

            // Determine velocity based on movespeed, and rotate it so that it's in the right direction.
            var movementVelocity = (tileMovement.Destination) - (targetTransform.LocalPosition);
            movementVelocity.Normalize();
            if (inputMover.Sprinting)
            {
                movementVelocity *= moveSpeedComponent?.CurrentSprintSpeed ??
                    MovementSpeedModifierComponent.DefaultBaseSprintSpeed;
            }
            else
            {
                movementVelocity *= moveSpeedComponent?.CurrentWalkSpeed ??
                    MovementSpeedModifierComponent.DefaultBaseWalkSpeed;
            }

            movementVelocity = parentRotation.RotateVec(movementVelocity);

            // Apply final velocity to physics body.
            PhysicsSystem.SetLinearVelocity(physicsUid, movementVelocity, body: physicsComponent);
            PhysicsSystem.SetAngularVelocity(physicsUid, 0, body: physicsComponent);
        }
    }

    /// <summary>
    /// Sets values on a TileMovementComponent designating that the slide has ended and sets it velocity to zero.
    /// </summary>
    /// <param name="uid">UID of the entity whose slide is being ended.</param>
    /// <param name="tileMovement">TileMovementComponent on the entity represented by UID.</param>
    private void EndSlide(EntityUid uid, TileMovementComponent tileMovement)
    {
        tileMovement.SlideActive = false;
        tileMovement.MovementKeyInitialDownTime = null;
        var physicsComponent = PhysicsQuery.GetComponent(uid);
        PhysicsSystem.SetLinearVelocity(uid, Vector2.Zero, body: physicsComponent);
        PhysicsSystem.SetAngularVelocity(uid, 0, body: physicsComponent);
    }

    /// <summary>
    /// Instantly snaps/teleports an entity to the center of the tile it is currently standing on based on the
    /// given grid. Does not trigger collisions on the way there, but does trigger collisions after the snap.
    /// </summary>
    /// <param name="uid">UID of entity to be snapped.</param>
    /// <param name="inputMover">InputMoverComponent on the entity to be snapped.</param>
    private void ForceSnapToTile(EntityUid uid, InputMoverComponent inputMover)
    {
        if (TryComp(inputMover.RelativeEntity, out TransformComponent? rel))
        {
            var targetTransform = Transform(uid);

            var localCoordinates = targetTransform.LocalPosition;
            var snappedCoordinates = SnapCoordinatesToTile(localCoordinates);

            if (!localCoordinates.EqualsApprox(snappedCoordinates) && targetTransform.ParentUid.IsValid())
            {
                _transform.SetLocalPosition(uid, snappedCoordinates);
            }

            PhysicsSystem.WakeBody(uid);
        }
    }

    /// <summary>
    /// Returns the movespeed of the given entity.
    /// </summary>
    /// <param name="uid">UID of the entity whose movespeed is being grabbed. May or may not have a MoveSpeedComponent.</param>
    /// <param name="sprinting">Whether the speed of the entity while sprinting should be grabbed.</param>
    /// <returns></returns>
    private float GetEntityMoveSpeed(EntityUid uid, bool sprinting)
    {
        var moveSpeedComponent = ModifierQuery.CompOrNull(uid);
        if (sprinting)
        {
            return moveSpeedComponent?.CurrentSprintSpeed ?? MovementSpeedModifierComponent.DefaultBaseSprintSpeed;
        }

        return moveSpeedComponent?.CurrentWalkSpeed ?? MovementSpeedModifierComponent.DefaultBaseWalkSpeed;
    }

    private float GetEntityFriction(
        InputMoverComponent inputMover,
        MovementSpeedModifierComponent? movementSpeedComponent,
        ContentTileDefinition? tileDef
    )
    {
        if (inputMover.HeldMoveButtons != MoveButtons.None || movementSpeedComponent?.FrictionNoInput == null)
        {
            return tileDef?.MobFriction ??
                movementSpeedComponent?.Friction ?? MovementSpeedModifierComponent.DefaultFriction;
        }

        return movementSpeedComponent.FrictionNoInput;
    }

    /// <summary>
    /// Sets the walk value on the given MoveButtons input to zero.
    /// </summary>
    /// <param name="input">The MoveButtons to edit.</param>
    /// <returns></returns>
    private MoveButtons StripWalk(MoveButtons input)
    {
        return input & ~MoveButtons.Walk;
    }

    /// <summary>
    /// Returns the given local coordinates snapped to the center of the tile it is currently on.
    /// </summary>
    /// <param name="input">Given coordinates to snap.</param>
    /// <returns>The closest tile center to the input.<returns>
    public static Vector2 SnapCoordinatesToTile(Vector2 input)
    {
        return new Vector2((int) Math.Floor(input.X) + 0.5f, (int) Math.Floor(input.Y) + 0.5f);
    }
    // Tile Movement Functions End
}