#nullable enable

using System;
using System.Linq;
using Sandbox;
using Sandbox.Diagnostics;

public class ReplicatorMelon : Component, Component.ICollisionListener
{
	private static readonly Logger Log = new Logger( nameof(ReplicatorMelon) );

	private readonly Random _random = new();

	// Constants
	private const float PathUpdateInterval = .5f;

	// Backing fields
	private GameObject? _target;
	private TimeSince _timeSinceGeneratedPath = 0;
	private TimeSince _timeSinceLastJump;
	private TimeSince _timeSinceLastJumpAttempt;

	// Public state
	public TimeSince LastTargetUpdate { get; private set; }

	// Component accessors
	private NavMeshAgent Agent => GetOrAddComponent<NavMeshAgent>();
	private Rigidbody RigidBody => GetOrAddComponent<Rigidbody>();

	// Tunable properties
	/// <summary>
	/// The base amount of torque to apply when moving the melon
	/// </summary>
	[Property] [Sync]
	public float BaseTorque { get; set; } = 20000000;

	/// <summary>
	/// The amount of torque to use while correcting for horizontal velocity.
	/// Lower values may cause the rollermine to orbit its target, and higher
	/// values will make it beeline directly at it.
	/// </summary>
	[Property] [Sync]
	public float CorrectionTorque { get; set; } = 11500;

	[Property] public float MaxTargetRange { get; set; } = 4096;

	[Property] public float SelfKnockbackForce { get; set; } = 80000;

	/// <summary>
	/// Applied to the melon each frame while it's jumping for "air-strafing"
	/// </summary>
	[Property]
	public float LeapForce { get; set; } = 600;

	/// <summary>
	/// Applies an upward force to the melon when it wants to move up to avoid getting stuck on stairs
	/// </summary>
	[Property]
	public float JumpForce { get; set; } = 50000;

	[Property] public float Damage { get; set; } = 150;

	[Property] public float JumpInterval { get; set; } = 2f;

	[Property] public SoundEvent? ImpactSound { get; set; }

	[Sync] public Vector3 WishVelocity { get; set; }

	[Sync] public Vector3 AdditionalForce { get; set; }

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

		if ( !Network.IsProxy )
		{
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
			
			float jumpChance = Vector3.Dot( (Agent.GetLookAhead( 10 ) - GameObject.WorldPosition).Normal, Vector3.Up );
			jumpChance = Math.Clamp( jumpChance * 5, 0f, 1f );
			// Log.Info(jumpChance);

			// === JUMP ===
			if ( _timeSinceLastJumpAttempt > JumpInterval && !float.IsNaN( jumpChance ) )
			{
				float rand = (float)_random.NextDouble();

				if ( rand < jumpChance )
				{
					Jump(new Vector3(0, 0, JumpForce));
				}

				// Randomize jump attempts
				_timeSinceLastJumpAttempt = 0 + Remap( (float)_random.NextDouble(), 0, 1, -1, 1 ) * .6f;
			}
			
			AdditionalForce = _timeSinceLastJump < 1 ? Agent.WishVelocity * LeapForce : Vector3.Zero;
		}

		Move( Agent.WishVelocity.Normal );

	}

	[Rpc.Broadcast]
	private void Jump( Vector3 velocity )
	{
		RigidBody.ApplyImpulse( velocity );
		_timeSinceLastJump = 0;
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
		
		RigidBody.ApplyForce(AdditionalForce);
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
		if ( IsProxy )
			return;
		
		GameObject other = collision.Other.GameObject;
		if ( !other.Tags.Has( "player" ) )
			return;

		var damageable = other.Components.GetInAncestorsOrSelf<IDamageable>();
		if ( damageable == null || _timeSinceLastRep <= 3 )
			return;


		Vector3 knockback = collision.Contact.Normal * SelfKnockbackForce;
		knockback.z = 20000;
		
		CopyClient(knockback);
		damageable.OnDamage( new DamageInfo( Damage, GameObject, GameObject ) );

		var copy = GameObject.Clone();
		copy.NetworkMode = NetworkMode.Snapshot;
		copy.WorldPosition = this.WorldPosition;
		copy.GetComponent<Rigidbody>().Velocity = RigidBody.Velocity;
		copy.Enabled = true;
		copy.NetworkSpawn();

		_timeSinceLastRep = 0;
	}

	[Rpc.Broadcast]
	private void CopyClient(Vector3 knockback)
	{
		Log.Trace( $"knokback: {knockback}" );
		RigidBody.ApplyImpulse( knockback );
		if ( ImpactSound != null )
			Sound.Play( ImpactSound, GameObject.WorldPosition );
	}

	

	private static float MeasureAngle( Vector2 a, Vector2 b )
	{
		float angleA = MathF.Atan2( a.y, a.x );
		float angleB = MathF.Atan2( b.y, b.x );
		return angleA - angleB;
	}

	private static float Remap( float value, float low1, float low2, float high1, float high2 )
	{
		return low2 + (high2 - low2) * ((value - low1) / (high1 - low1));
	}
}
