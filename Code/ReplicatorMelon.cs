using System;
using System.Linq;
using Sandbox;
using Sandbox.Diagnostics;
using Sandbox.Navigation;

public partial class ReplicatorMelon : Component, Component.ICollisionListener
{
	
	private static readonly Logger log = new Logger(nameof(ReplicatorMelon));

	private GameObject? _target;
	public TimeSince LastTargetUpdate { get; private set; }
	

	/// <summary>
	/// The base amount of torque to apply when moving the melon
	/// </summary>
	[Property]
	public float BaseTorque { get; set; } = 15000000;
	
	/// <summary>
	/// The amount of torque to use while correcting for horizontal velocity.
	/// Lower values may cause the rollermine to orbit its target, and higher
	/// values will make it beeline directly at it.
	/// </summary>
	[Property]
	public float CorrectionTorque { get; set; } = 30000;
	
	[Property]
	public float MaxTargetRange { get; set; } = 4096;
	
	[Property]
	public float SelfKnockbackForce { get; set; } = 80000;
	
	private NavMeshAgent Agent => GetComponent<NavMeshAgent>();
	private Rigidbody RigidBody => GetComponent<Rigidbody>();
	
	private const float MAX_TORQUE_FACTOR = 5;
	
	public GameObject? Target
	{
		get => _target;
		set
		{
			_target = value;
			LastTargetUpdate = 0;
		}
	}

	public float TargetInterval { get; set; } = 5;
	
	private const float PATH_UPDATE_INTERVAL = 1f;
	private const int PATH_ERROR_ALLOWANCE = 64;

	private TimeSince TimeSinceGeneratedPath = 0;
	private int CurrentPathSegment;
	private NavMeshPath? Path;

	protected override void OnAwake()
	{
		base.OnAwake();
		RigidBody.CollisionEventsEnabled = true;
		RigidBody.CollisionUpdateEventsEnabled = true;
	}

	protected override void OnFixedUpdate()
	{
		base.OnFixedUpdate();
		if ( ShouldUpdateTarget() )
		{
			UpdateTarget();
		}

		if ( Agent.TargetPosition == null || TimeSinceGeneratedPath > PATH_UPDATE_INTERVAL)
		{
			if ( Target != null )
			{
				Agent.MoveTo(Target.WorldPosition);
			}
			else
			{
				Agent.Stop();
			}
		}

		var currentLink = Agent.CurrentLinkTraversal;
		if ( currentLink.HasValue )
		{
			log.Info($"Current link: {currentLink}");
			MoveTowards(currentLink.Value.LinkExitPosition);
		}
	}

	private bool ShouldUpdateTarget()
	{
		return LastTargetUpdate > TargetInterval || !CanTarget(Target);
	}

	private void UpdateTarget()
	{
		log.Trace("Updating melon target");
			
		GameObject? closest = null;
		foreach ( var obj in Scene.GetAllObjects( true ).Where( CanTarget ) )
		{
			if (obj == null) continue;
			if ( closest == null || obj.WorldPosition.DistanceSquared( this.WorldPosition ) < closest.WorldPosition.DistanceSquared( this.WorldPosition ) ) 
			{
				closest = obj;
			}
		}
		log.Info(closest);
		Target = closest;
	}
	

	private bool CanTarget( GameObject? target )
	{
		return target != null
		       && target.IsValid
		       && target.Tags.Has( "player" )
		       && GameObject.WorldPosition.DistanceSquared(target.WorldPosition) <= MaxTargetRange * MaxTargetRange;
	}
	
	public void OnCollisionStart( Collision collision )
	{
		GameObject other = collision.Other.GameObject;
		if ( CanTarget( other ) )
		{
			other.Components.GetInAncestorsOrSelf<IDamageable>()?.OnDamage(new DamageInfo(50, GameObject, GameObject));

			Vector3 knockback = collision.Contact.Normal.WithZ( 1 ) * SelfKnockbackForce;
			GetComponent<Rigidbody>()?.ApplyImpulse(knockback);
		}
	}

	public void MoveTowards( Vector3 targetPos )
	{
		targetPos.z = this.WorldPosition.z;
		
		Vector3 normal = (targetPos - this.WorldPosition).Normal;
		Vector2 normal2D = new Vector2( normal.x, normal.y );

		var axis = normal.RotateAround( new Vector3( 0, 0, 0 ), Rotation.FromYaw( 90 ) );
		float torque = BaseTorque * .6f;

		RigidBody.ApplyTorque( axis * torque );
		
		// Determine angle to account for existing velocity
		Vector2 velocity2D = new Vector2(RigidBody.Velocity.x, RigidBody.Velocity.y);
		float angle = MeasureAngle(velocity2D, normal2D);

		float correctionMagnitude = velocity2D.Length * MathF.Sin( angle );
		RigidBody.ApplyTorque(normal * correctionMagnitude * CorrectionTorque);
	}

	private static float MeasureAngle( Vector2 a, Vector2 b )
	{
		float angleA = MathF.Atan2( a.y, a.x );
		float angleB = MathF.Atan2( b.y, b.x );
		return angleA - angleB;
	}
}
