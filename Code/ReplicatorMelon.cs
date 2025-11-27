#nullable enable

using System;
using System.Linq;
using Sandbox;
using Sandbox.Diagnostics;

public class ReplicatorMelon : Component, Component.ICollisionListener
{
	private static readonly Logger Log = new Logger( nameof(ReplicatorMelon) );

	// Constants
	private const float PathUpdateInterval = .5f;

	// Backing fields
	private GameObject? _target;
	private TimeSince _timeSinceGeneratedPath = 0;
	private TimeSince _timeSinceLastJump;

	// Public state
	public TimeSince LastTargetUpdate { get; private set; }

	// Component accessors
	private NavMeshAgent Agent => GetOrAddComponent<NavMeshAgent>();
	private Rigidbody RigidBody => GetOrAddComponent<Rigidbody>();

	// Tunable properties
	/// <summary>
	/// The base amount of torque to apply when moving the melon
	/// </summary>
	[Property]
	public float BaseTorque { get; set; } = 18000000;

	/// <summary>
	/// The amount of torque to use while correcting for horizontal velocity.
	/// Lower values may cause the rollermine to orbit its target, and higher
	/// values will make it beeline directly at it.
	/// </summary>
	[Property]
	public float CorrectionTorque { get; set; } = 12000;

	[Property] public float MaxTargetRange { get; set; } = 4096;

	[Property] public float SelfKnockbackForce { get; set; } = 80000;

	[Property] public float LeapForce { get; set; } = 600;

	/// <summary>
	/// Applies an upward force to the melon when it wants to move up to avoid getting stuck on stairs
	/// </summary>
	[Property]
	public float JumpForce { get; set; } = 45000;

	[Property]
	public float Damage { get; set; } = 150;

	[Property] public float JumpInterval { get; set; } = 2.5f;

	// Targeting
	public GameObject? Target
	{
		get => _target;
		set
		{
			_target = value;
			LastTargetUpdate = 0;
		}
	}

	[Property] public float TargetInterval { get; set; } = 5;

	private TimeSince _timeSinceLastRep = 2;

	protected override void OnAwake()
	{
		base.OnAwake();
		RigidBody.CollisionEventsEnabled = true;
		RigidBody.CollisionUpdateEventsEnabled = true;
	}


	protected override void OnFixedUpdate()
	{
		base.OnFixedUpdate();

		if ( Network.IsProxy ) return;
		if ( ShouldUpdateTarget() )
		{
			UpdateTarget();
		}

		Agent.SetAgentPosition( GameObject.WorldPosition );
		if ( Agent.TargetPosition == null || _timeSinceGeneratedPath > PathUpdateInterval )
		{
			if ( Target != null )
			{
				Agent.MoveTo( Target.WorldPosition );
			}
			else
			{
				Agent.Stop();
			}

			_timeSinceGeneratedPath = 0;
		}


		if ( _timeSinceLastJump > JumpInterval )
		{
			float finalJumpForce = JumpForce + MathF.Min( Agent.GetLookAhead( 10 ).z * 500, 0 );

			RigidBody.ApplyImpulse( new Vector3( 0, 0, JumpForce ) );
			_timeSinceLastJump = Random.Shared.Float() * 2 - 1;
		}

		Move( Agent.WishVelocity.Normal );
		// Also apply a force to help it in the air
		if ( Target != null )
		{
			RigidBody.ApplyForce( Agent.WishVelocity * (_timeSinceLastJump < 1 ? LeapForce : 0) );
		}
	}

	private bool ShouldUpdateTarget()
	{
		return LastTargetUpdate > TargetInterval || !CanTarget( Target );
	}

	private void UpdateTarget()
	{
		GameObject? closest = null;
		foreach ( var obj in Scene.GetAllObjects( true ).Where( CanTarget ) )
		{
			if ( obj == null ) continue;
			if ( closest == null || obj.WorldPosition.DistanceSquared( this.WorldPosition ) <
			    closest.WorldPosition.DistanceSquared( this.WorldPosition ) )
			{
				closest = obj;
			}
		}

		Log.Trace( $"Targeting {closest}" );
		Target = closest;
	}


	private bool CanTarget( GameObject? target )
	{
		return target != null
		       && target.IsValid
		       && target.Tags.Has( "player" )
		       && target.GetComponent<PlayerController>() != null
		       && GameObject.WorldPosition.DistanceSquared( target.WorldPosition ) <= MaxTargetRange * MaxTargetRange;
	}

	public void OnCollisionStart( Collision collision )
	{
		GameObject other = collision.Other.GameObject;
		if ( !other.Tags.Has( "player" ) )
			return;

		var damageable = other.Components.GetInAncestorsOrSelf<IDamageable>();
		if ( damageable == null || _timeSinceLastRep <= 3 )
			return;


		Vector3 knockback = collision.Contact.Normal * SelfKnockbackForce;
		knockback.z = 20000;
		Log.Info( knockback );
		RigidBody.ApplyImpulse( knockback );
		damageable.OnDamage( new DamageInfo( Damage, GameObject, GameObject ) );

		var copy = GameObject.Clone();
		copy.NetworkMode = NetworkMode.Snapshot;
		copy.WorldPosition = this.WorldPosition;
		copy.GetComponent<Rigidbody>().Velocity = RigidBody.Velocity;
		copy.Enabled = true;
		copy.NetworkSpawn();

		_timeSinceLastRep = 0;
	}

	private void Move( Vector3 direction )
	{
		direction.z = 0;

		Vector2 normal2D = new Vector2( direction.x, direction.y );

		var axis = direction.RotateAround( new Vector3( 0, 0, 0 ), Rotation.FromYaw( 90 ) );
		float torque = BaseTorque * .6f;

		RigidBody.ApplyTorque( axis * torque );

		// Determine angle to account for existing velocity
		Vector2 velocity2D = new Vector2( RigidBody.Velocity.x, RigidBody.Velocity.y );
		float angle = MeasureAngle( velocity2D, normal2D );

		float correctionMagnitude = velocity2D.Length * MathF.Sin( angle );

		RigidBody.ApplyTorque( direction * correctionMagnitude * CorrectionTorque );
	}

	private static float MeasureAngle( Vector2 a, Vector2 b )
	{
		float angleA = MathF.Atan2( a.y, a.x );
		float angleB = MathF.Atan2( b.y, b.x );
		return angleA - angleB;
	}
}
