using System;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public sealed class PhysicsBodyTests
{
    // ── helpers ──────────────────────────────────────────────────────────

    private static PhysicsBody MakeAirborne()
    {
        var body = new PhysicsBody
        {
            State = PhysicsStateFlags.Gravity | PhysicsStateFlags.ReportCollisions,
        };
        body.TransientState = TransientStateFlags.Active;
        return body;
    }

    private static PhysicsBody MakeGrounded()
    {
        var body = new PhysicsBody
        {
            State = PhysicsStateFlags.Gravity | PhysicsStateFlags.ReportCollisions,
        };
        body.TransientState = TransientStateFlags.Contact | TransientStateFlags.OnWalkable | TransientStateFlags.Active;
        return body;
    }


    [Fact]
    public void calc_acceleration_airborne_gravity_sets_minus_9_8_on_z()
    {
        var body = MakeAirborne();
        body.calc_acceleration();

        Assert.Equal(0f, body.Acceleration.X);
        Assert.Equal(0f, body.Acceleration.Y);
        Assert.Equal(-9.8f, body.Acceleration.Z, precision: 6);
    }

    [Fact]
    public void calc_acceleration_grounded_zeros_acceleration_and_omega()
    {
        var body = MakeGrounded();
        body.Acceleration = new Vector3(1f, 2f, 3f);
        body.Omega = new Vector3(0.5f, 0.5f, 0.5f);
        body.calc_acceleration();

        Assert.Equal(Vector3.Zero, body.Acceleration);
        Assert.Equal(Vector3.Zero, body.Omega);
    }

    [Fact]
    public void calc_acceleration_no_gravity_flag_zeros_acceleration()
    {
        var body = new PhysicsBody
        {
            State = PhysicsStateFlags.None,  // no Gravity flag
            TransientState = TransientStateFlags.Active,
        };
        body.Acceleration = new Vector3(0f, 0f, -9.8f);
        body.calc_acceleration();

        Assert.Equal(Vector3.Zero, body.Acceleration);
    }

    [Fact]
    public void calc_acceleration_sledding_airborne_still_applies_gravity()
    {
        // Sledding but not grounded — gravity still applies
        var body = new PhysicsBody
        {
            State = PhysicsStateFlags.Gravity | PhysicsStateFlags.Sledding,
            TransientState = TransientStateFlags.Active,
        };
        body.calc_acceleration();

        Assert.Equal(-9.8f, body.Acceleration.Z, precision: 6);
    }

    // ════════════════════════════════════════════════════════════════════
    // UpdatePhysicsInternal — Euler integration
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void UpdatePhysicsInternal_integrates_position_correctly_one_step()
    {
        // Analytical: x(t) = x0 + v0*t + 0.5*a*t²
        // With v0=(1,0,0), a=(0,0,-9.8), dt=0.1
        //   x = 0.1
        //   z = 0.5 * (-9.8) * 0.01 = -0.049
        var body = MakeAirborne();
        body.Velocity = new Vector3(1f, 0f, 0f);
        body.Acceleration = new Vector3(0f, 0f, -9.8f);

        body.UpdatePhysicsInternal(0.1f);

        Assert.Equal(0.1f, body.Position.X, precision: 5);
        Assert.Equal(0f,   body.Position.Y, precision: 5);
        // 0.5 * (-9.8) * 0.01 = -0.049
        Assert.Equal(-0.049f, body.Position.Z, precision: 4);
    }

    [Fact]
    public void UpdatePhysicsInternal_velocity_updated_by_acceleration_times_dt()
    {
        var body = MakeAirborne();
        body.Velocity = new Vector3(0f, 0f, 0f);
        body.Acceleration = new Vector3(0f, 0f, -9.8f);

        body.UpdatePhysicsInternal(0.5f);

        // velocity += accel * dt = (0, 0, -9.8 * 0.5) = (0, 0, -4.9)
        Assert.Equal(0f, body.Velocity.X, precision: 5);
        Assert.Equal(0f, body.Velocity.Y, precision: 5);
        Assert.Equal(-4.9f, body.Velocity.Z, precision: 4);
    }

    [Fact]
    public void UpdatePhysicsInternal_multiple_frames_accumulates_correctly()
    {
        // Free-fall from rest under gravity for N frames of dt each.
        // Analytical z(t) = 0.5 * g * t²  where g = -9.8
        // After 10 frames of 0.1 s each (total t=1.0 s):
        //   z = 0.5 * (-9.8) * 1.0 = -4.9
        // The Euler integrator accumulates small truncation error, so allow 2% tolerance.
        var body = MakeAirborne();
        body.Velocity = Vector3.Zero;
        body.Acceleration = new Vector3(0f, 0f, PhysicsBody.Gravity);

        const int frames = 10;
        const float dt = 0.1f;
        for (int i = 0; i < frames; i++)
            body.UpdatePhysicsInternal(dt);

        float expected = 0.5f * PhysicsBody.Gravity * (frames * dt) * (frames * dt);
        Assert.True(MathF.Abs(body.Position.Z - expected) < 0.15f,
            $"Expected z ≈ {expected:F4}, got {body.Position.Z:F4}");
    }

    [Fact]
    public void UpdatePhysicsInternal_zero_velocity_clears_active_flag_when_grounded()
    {
        var body = MakeGrounded();
        body.Velocity = Vector3.Zero;
        body.TransientState |= TransientStateFlags.Active;

        body.UpdatePhysicsInternal(0.1f);

        Assert.False(body.IsActive);
    }

    [Fact]
    public void UpdatePhysicsInternal_zeroes_small_velocity_even_when_airborne()
    {
        var body = MakeAirborne();
        body.set_velocity(new Vector3(0.1f, 0f, 0f));  // < 0.25 m/s
        body.Acceleration = new Vector3(0f, 0f, PhysicsBody.Gravity);

        body.UpdatePhysicsInternal(1f / 30f);

        Assert.True(MathF.Abs(body.Velocity.X) < 1e-4f, $"X not zeroed: {body.Velocity.X}");
        Assert.True(body.Velocity.Z < 0f, $"gravity did not accumulate: {body.Velocity.Z}");
    }


    [Fact]
    public void TransientStateFlags_has_stationary_bits()
    {
        Assert.Equal(0x10u, (uint)TransientStateFlags.StationaryFall);
        Assert.Equal(0x20u, (uint)TransientStateFlags.StationaryStop);
        Assert.Equal(0x40u, (uint)TransientStateFlags.StationaryStuck);
    }

    [Fact]
    public void PhysicsBody_has_fsf_and_cached_velocity_defaults()
    {
        var body = new PhysicsBody();
        Assert.Equal(0, body.FramesStationaryFall);
        Assert.Equal(Vector3.Zero, body.CachedVelocity);
    }


    [Fact]
    public void set_velocity_below_max_stores_velocity_unchanged()
    {
        var body = new PhysicsBody();
        var v = new Vector3(10f, 5f, 2f);
        body.set_velocity(v);

        Assert.Equal(v, body.Velocity);
    }

    [Fact]
    public void set_velocity_above_max_clamps_to_MaxVelocity_magnitude()
    {
        var body = new PhysicsBody();
        // velocity with magnitude > 50
        var v = new Vector3(100f, 0f, 0f);
        body.set_velocity(v);

        Assert.True(body.Velocity.Length() <= PhysicsBody.MaxVelocity + 1e-4f,
            $"Velocity magnitude {body.Velocity.Length()} exceeds MaxVelocity {PhysicsBody.MaxVelocity}");
        Assert.Equal(PhysicsBody.MaxVelocity, body.Velocity.Length(), precision: 4);
    }

    [Fact]
    public void set_velocity_diagonal_above_max_clamps_and_preserves_direction()
    {
        var body = new PhysicsBody();
        var dir = Vector3.Normalize(new Vector3(3f, 4f, 0f)); // unit vector
        var v = dir * 80f;  // magnitude = 80 > 50
        body.set_velocity(v);

        Assert.Equal(PhysicsBody.MaxVelocity, body.Velocity.Length(), precision: 3);
        // Direction should be preserved
        var resultDir = Vector3.Normalize(body.Velocity);
        Assert.Equal(dir.X, resultDir.X, precision: 4);
        Assert.Equal(dir.Y, resultDir.Y, precision: 4);
    }

    [Fact]
    public void set_velocity_sets_active_flag()
    {
        var body = new PhysicsBody();
        body.TransientState = TransientStateFlags.None;
        body.set_velocity(new Vector3(1f, 0f, 0f));

        Assert.True(body.IsActive);
    }

    [Fact]
    public void set_velocity_exactly_at_max_is_not_clamped()
    {
        var body = new PhysicsBody();
        var v = new Vector3(PhysicsBody.MaxVelocity, 0f, 0f);
        body.set_velocity(v);

        Assert.Equal(v.X, body.Velocity.X, precision: 4);
        Assert.Equal(0f,  body.Velocity.Y, precision: 4);
        Assert.Equal(0f,  body.Velocity.Z, precision: 4);
    }

    // ════════════════════════════════════════════════════════════════════
    // set_local_velocity — body→world transform
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void set_local_velocity_identity_orientation_passes_through()
    {
        var body = new PhysicsBody { Orientation = Quaternion.Identity };
        body.set_local_velocity(new Vector3(1f, 0f, 0f));

        Assert.Equal(1f, body.Velocity.X, precision: 5);
        Assert.Equal(0f, body.Velocity.Y, precision: 5);
        Assert.Equal(0f, body.Velocity.Z, precision: 5);
    }

    [Fact]
    public void set_local_velocity_90_degree_yaw_rotates_forward_to_right()
    {
        // A 90° CCW rotation around Z maps +X in local space to +Y in world space.
        var body = new PhysicsBody
        {
            Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f)
        };
        body.set_local_velocity(new Vector3(1f, 0f, 0f));

        // After 90° yaw: local +X becomes world +Y (approximately)
        Assert.True(MathF.Abs(body.Velocity.X) < 1e-4f, $"Expected Vx≈0, got {body.Velocity.X}");
        Assert.True(MathF.Abs(body.Velocity.Y - 1f) < 1e-4f, $"Expected Vy≈1, got {body.Velocity.Y}");
        Assert.True(MathF.Abs(body.Velocity.Z) < 1e-4f, $"Expected Vz≈0, got {body.Velocity.Z}");
    }

    [Fact]
    public void set_local_velocity_180_degree_yaw_reverses_horizontal_forward()
    {
        var body = new PhysicsBody
        {
            Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI)
        };
        body.set_local_velocity(new Vector3(1f, 0f, 0f));

        Assert.True(MathF.Abs(body.Velocity.X + 1f) < 1e-4f, $"Expected Vx≈-1, got {body.Velocity.X}");
        Assert.True(MathF.Abs(body.Velocity.Y) < 1e-4f, $"Expected Vy≈0, got {body.Velocity.Y}");
    }

    [Fact]
    public void set_local_velocity_magnitude_preserved_after_rotation()
    {
        var body = new PhysicsBody
        {
            Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.23f)
        };
        var localVel = new Vector3(3f, 4f, 0f);
        body.set_local_velocity(localVel);

        Assert.Equal(localVel.Length(), body.Velocity.Length(), precision: 4);
    }

    // ════════════════════════════════════════════════════════════════════
    // set_on_walkable
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void set_on_walkable_true_sets_OnWalkable_flag()
    {
        var body = MakeAirborne();
        body.set_on_walkable(true);

        Assert.True(body.OnWalkable);
    }

    [Fact]
    public void set_on_walkable_false_clears_OnWalkable_flag()
    {
        var body = MakeGrounded();
        body.set_on_walkable(false);

        Assert.False(body.OnWalkable);
    }

    [Fact]
    public void set_on_walkable_true_also_calls_calc_acceleration_zeroing_accel()
    {
        var body = new PhysicsBody
        {
            State = PhysicsStateFlags.Gravity | PhysicsStateFlags.ReportCollisions,
            TransientState = TransientStateFlags.Contact,
            Acceleration = new Vector3(0f, 0f, -9.8f),
        };
        body.set_on_walkable(true);

        Assert.Equal(Vector3.Zero, body.Acceleration);
    }

    [Fact]
    public void set_on_walkable_false_allows_gravity_to_apply()
    {
        var body = MakeGrounded();
        body.set_on_walkable(false);

        Assert.Equal(-9.8f, body.Acceleration.Z, precision: 6);
    }


    [Fact]
    public void calc_friction_not_on_walkable_does_nothing()
    {
        var body = MakeAirborne();
        body.Velocity = new Vector3(5f, 0f, 0f);
        var before = body.Velocity;
        body.calc_friction(0.1f, body.Velocity.LengthSquared());

        Assert.Equal(before, body.Velocity);
    }

    [Fact]
    public void calc_friction_velocity_parallel_to_ground_reduces_magnitude()
    {
        var body = MakeGrounded();
        body.GroundNormal = Vector3.UnitZ;
        body.Velocity = new Vector3(5f, 0f, -0.1f);
        float mag2 = body.Velocity.LengthSquared();

        body.calc_friction(0.1f, mag2);

        // Speed should be reduced by friction
        Assert.True(body.Velocity.Length() < new Vector3(5f, 0f, 0f).Length(),
            "Friction should reduce velocity magnitude");
    }

    [Fact]
    public void calc_friction_velocity_moving_away_from_normal_no_change()
    {
        // dot(GroundNormal=(0,0,1), velocity=(5,0,1)) = 1 > 0 → no friction
        var body = MakeGrounded();
        body.GroundNormal = Vector3.UnitZ;
        body.Velocity = new Vector3(5f, 0f, 1f);  // moving up = away from ground
        var before = body.Velocity;
        float mag2 = body.Velocity.LengthSquared();

        body.calc_friction(0.1f, mag2);

        Assert.Equal(before, body.Velocity);
    }

    [Fact]
    public void calc_friction_zero_friction_coefficient_no_reduction()
    {
        var body = MakeGrounded();
        body.GroundNormal = Vector3.UnitZ;
        body.Velocity = new Vector3(5f, 0f, -0.01f);
        body.Friction = 0f;  // frictionless surface
        float mag2 = body.Velocity.LengthSquared();

        body.calc_friction(0.1f, mag2);

        Assert.True(body.Velocity.Length() > 4.9f,
            $"Zero friction: speed {body.Velocity.Length()} should stay near 5");
    }

    [Fact]
    public void calc_friction_removes_normal_component_from_velocity()
    {
        // Velocity = (1, 0, -1), GroundNormal = (0, 0, 1)
        // dot = -1 → velocity -= (-1) * (0,0,1) = velocity + (0,0,1) → (1, 0, 0)
        var body = MakeGrounded();
        body.GroundNormal = Vector3.UnitZ;
        body.Friction = 0f;  // no friction to isolate normal-removal behavior
        body.Velocity = new Vector3(1f, 0f, -1f);
        float mag2 = body.Velocity.LengthSquared();

        body.calc_friction(1.0f, mag2);

        Assert.True(MathF.Abs(body.Velocity.Z) < 1e-4f,
            $"Normal component should be removed; Vz = {body.Velocity.Z}");
        Assert.Equal(1f, body.Velocity.X, precision: 4);
    }


    [Fact]
    public void calc_friction_dot_between_zero_and_quarter_now_engages_friction()
    {
        var body = MakeGrounded();
        body.GroundNormal = Vector3.UnitZ;
        body.Friction = 0.95f;
        body.Velocity = new Vector3(5f, 0f, 0.1f);
        float mag2 = body.Velocity.LengthSquared();

        body.calc_friction(1f / 60f, mag2);

        Assert.True(body.Velocity.Length() < 5f,
            "Retail's 0.25f threshold means dot=0.1 (below 0.25) engages friction, " +
            "unlike the old 0.0 threshold which would have returned early here.");
    }

    [Fact]
    public void calc_friction_dot_at_quarter_threshold_returns_early_no_change()
    {
        var body = MakeGrounded();
        body.GroundNormal = Vector3.UnitZ;
        body.Velocity = new Vector3(5f, 0f, 0.25f);
        var before = body.Velocity;
        float mag2 = body.Velocity.LengthSquared();

        body.calc_friction(1f / 60f, mag2);

        Assert.Equal(before, body.Velocity);
    }

    [Fact]
    public void GroundedRootMotion_FrictionThreshold_DoesNotHammerLocomotionTests()
    {
        var body = MakeGrounded();
        body.GroundNormal = Vector3.UnitZ;
        body.Friction = 0.95f;
        // Root-motion path's exact per-tick shape: horizontal zeroed, only
        // the world Z survives (a small residual downward settle velocity).
        body.Velocity = new Vector3(0f, 0f, -0.05f);

        body.calc_friction(1f / 60f, body.Velocity.LengthSquared());

        Assert.Equal(0f, body.Velocity.X, precision: 5);
        Assert.Equal(0f, body.Velocity.Y, precision: 5);
        Assert.True(MathF.Abs(body.Velocity.Z) < 0.05f,
            $"Root-motion horizontal speed must stay exactly at full (zero) " +
            $"speed under the new threshold; got Velocity={body.Velocity}");
    }

    [Fact]
    public void calc_friction_sledding_state_gate_reachable_with_new_threshold()
    {
        var body = MakeGrounded();
        body.GroundNormal = Vector3.UnitZ;
        body.State |= PhysicsStateFlags.Sledding;
        body.Velocity = new Vector3(3f, 0f, -0.5f);   // velocityMag2 = 9.25, >= 6.25
        float mag2 = body.Velocity.LengthSquared();

        body.calc_friction(1f / 60f, mag2);

        Assert.True(body.Velocity.Length() > 2.9f,
            $"Fast near-flat sledding should use the light 0.2f friction override; " +
            $"got speed {body.Velocity.Length()}");
    }


    [Fact]
    public void calc_friction_sledding_fast_override_engages_at_5_degrees_from_flat()
    {
        float cos5 = MathF.Cos(5f * MathF.PI / 180f);
        float sin5 = MathF.Sin(5f * MathF.PI / 180f);
        var body = MakeGrounded();
        body.GroundNormal = new Vector3(sin5, 0f, cos5);
        body.State |= PhysicsStateFlags.Sledding;
        body.Velocity = new Vector3(0f, 3f, 0f);
        float mag2 = body.Velocity.LengthSquared();
        const float dt = 1f / 60f;

        body.calc_friction(dt, mag2);

        // friction = 0.2f (light) expected: scalar = (1 - 0.2)^dt.
        float expectedSpeed = 3f * MathF.Pow(0.8f, dt);
        Assert.Equal(expectedSpeed, body.Velocity.Length(), precision: 3);
    }

    [Fact]
    public void calc_friction_sledding_fast_override_does_not_engage_at_15_degrees_from_flat()
    {
        float cos15 = MathF.Cos(15f * MathF.PI / 180f);
        float sin15 = MathF.Sin(15f * MathF.PI / 180f);
        var body = MakeGrounded();
        body.GroundNormal = new Vector3(sin15, 0f, cos15);
        body.State |= PhysicsStateFlags.Sledding;
        body.Velocity = new Vector3(0f, 3f, 0f);
        float mag2 = body.Velocity.LengthSquared();
        const float dt = 1f / 60f;

        body.calc_friction(dt, mag2);

        float expectedSpeed = 3f * MathF.Pow(1f - PhysicsBody.DefaultFriction, dt);
        Assert.Equal(expectedSpeed, body.Velocity.Length(), precision: 3);
    }

    // ════════════════════════════════════════════════════════════════════
    // update_object — per-frame driver
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void update_object_dt_below_min_quantum_accumulates_without_advancing()
    {
        var body = MakeAirborne();
        body.Velocity = new Vector3(1f, 0f, 0f);
        body.Acceleration = Vector3.Zero;
        body.LastUpdateTime = 0.0;

        // Advance by less than MinQuantum — should be a no-op
        body.update_object(PhysicsBody.MinQuantum * 0.5);

        Assert.Equal(Vector3.Zero, body.Position);
        Assert.Equal(0d, body.LastUpdateTime);
    }

    [Fact]
    public void update_object_dt_above_huge_quantum_consumes_time_without_simulating()
    {
        var body = MakeAirborne();
        body.Velocity = new Vector3(1f, 0f, 0f);
        body.Acceleration = Vector3.Zero;
        body.LastUpdateTime = 0.0;

        body.update_object(PhysicsBody.HugeQuantum + 0.5);

        Assert.Equal(Vector3.Zero, body.Position);
        Assert.Equal(PhysicsBody.HugeQuantum + 0.5, body.LastUpdateTime, precision: 10);
    }

    [Fact]
    public void update_object_advances_position_over_valid_dt()
    {
        var body = MakeAirborne();
        // No friction or gravity interference — just pure horizontal velocity
        body.State = PhysicsStateFlags.None;  // no gravity
        body.Velocity = new Vector3(10f, 0f, 0f);
        body.LastUpdateTime = 0.0;

        double dt = 0.1;
        body.update_object(dt);

        // x ≈ 10 * 0.1 = 1.0  (ignoring sub-step rounding)
        Assert.True(body.Position.X > 0f, "Position should have advanced");
    }

    [Fact]
    public void update_object_updates_LastUpdateTime()
    {
        var body = MakeAirborne();
        body.LastUpdateTime = 0.0;
        body.State = PhysicsStateFlags.None;

        double t = 0.05;
        body.update_object(t);

        Assert.Equal(t, body.LastUpdateTime, precision: 10);
    }

    [Fact]
    public void update_object_micro_fragment_is_consumed()
    {
        var body = MakeAirborne();

        body.update_object(PhysicsGlobals.EPSILON * 0.5);

        Assert.Equal(PhysicsGlobals.EPSILON * 0.5, body.LastUpdateTime, precision: 10);
        Assert.Equal(Vector3.Zero, body.Position);
    }

    [Fact]
    public void update_object_gravity_free_fall_accumulates_downward_velocity()
    {
        var body = MakeAirborne();
        // Let it fall for one valid quantum
        body.LastUpdateTime = 0.0;
        double dt = PhysicsBody.MinQuantum * 2; // > MinQuantum but < HugeQuantum

        body.update_object(dt);

        // After one step velocity should be negative Z
        Assert.True(body.Velocity.Z < 0f,
            $"Gravity should produce negative Z velocity; got {body.Velocity.Z}");
    }
}
