//! The frame itself, driven from a per-frame game task: size, ground speed, the energy gauge,
//! Quick Boost, Assault Boost (hold) and AC6-style flight.
//!
//! Design (see sheets/frames.json; every movement number there is read from the real AC6 params by tools/ac6_numbers.py):
//! EN is the FP bar.
//! - Quick Boost: tap dodge; spends EN and adds a burst along the way you are moving (on the ground it rides on
//!   the game's own roll, in the air it follows the stick).
//! - Boost: hold dodge after the dash; ground speed eases up to the booster's dash speed (air: fly boost speed), EN drains,
//!   and on release the frame brakes along its momentum.
//! - Vertical boost: hold jump while airborne; climb eases to the booster's upper speed, the stick steers at the slow
//!   upper-boost horizontal speed. Let go and AC6's MovementGravity takes over; horizontal speed bleeds off at the fly brake. Gravity is off and fall damage suppressed while airborne under our control,
//!   because writing a position under normal gravity ends in a scripted fall death.
//! - Running EN to zero overheats the frame until the generator's empty delay has passed.
//! Input works with a pad stick AND the keyboard (digital keys read as full deflection), and the stick->world
//! mapping is learned from how the game itself moves the player on the ground, so it does not depend on any
//! assumption about the camera matrix. Every number comes from the sheet through frames_gen.rs.

use crate::frames_gen::{Frame, AIM_HEIGHT_M, DEBUG_SFX_COUNT, FRAMES, LUNGE_MPS, LUNGE_S, MUZZLE_FORWARD_M, SIZE_SCALE};
use crate::log;
use eldenring::cs::{BulletSpawnData, CSBulletManager, CSCamera, CSHavokMan, CSTaskGroupIndex, CSTaskImp, PlayerIns, UserInputKey, WorldChrMan};
use eldenring::fd4::{FD4PadManager, FD4TaskData};
use eldenring::position::{HavokPosition, PositionDelta};
use fromsoftware_shared::{F32Vector4, FromStatic, RecurringTask, SharedTaskImpExt};
use std::sync::Mutex;
use std::time::{Duration, Instant};

/// Havok ray filter that hits map collision.
const MAP_RAY_FILTER: u32 = 0x2000058;
/// How far in front of the frame a wall stops a horizontal move (metres).
const WALL_MARGIN: f32 = 1.2;
/// Frames after the dodge press before we read which way the roll is moving.
const DIRECTION_FRAMES: u32 = 3;
/// Seconds fall damage stays off after flight ends (the last bit of descent).
const FALL_GRACE: f32 = 1.5;
/// Bullet row of the blade slash bolt (BULLET_ID0 + 100 in build_regulation.py).
const BLADE_BULLET_ID: i32 = 99910100;
/// Seconds the dodge button must be held before the boost starts (the tap dash comes first).
const HOLD_AFTER: f32 = 0.25;
/// Number of stick->world hypotheses (2 heading sources x 8 axis arrangements).
const HYPS: usize = 16;

struct State {
    last_tick: Option<Instant>,
    frame_idx: Option<usize>,
    heartbeat: u32,
    scaled_logged: bool,
    notes_logged: u32,

    // Quick Boost
    boost_left: f32,
    boost_total: f32,
    boost_frames: u32,
    boost_dir: Option<(f32, f32)>,
    boost_start: (f32, f32, f32),
    boost_travelled: f32,
    boost_cost: f32,
    cooldown: f32,

    // energy gauge (fractional FP carried between frames)
    since_spend: f32,
    fp_carry: f32,
    en_carry_drain: f32,
    overheated: bool,
    empty_timer: f32,

    // ground speed: where we left the player last frame (so the game's own movement can be told apart from ours)
    last_pos: Option<(f32, f32, f32)>,
    speed_logged: bool,

    // world settle: seconds since the player last appeared/teleported (flight is not allowed until the world is stable)
    settle: f32,
    gravity_logged: bool,

    last_anim: i32,
    anim_logged: u32,
    air_t: f32,
    block_ok_now: bool,
    last_bid: i32,
    hp_logged: u32,
    ground_frames: u32,
    land_cool: f32,
    last_hp: i32,

    // weapons
    rounds: i32,
    back_rounds: i32,
    back_cd_r: f32,
    back_cd_l: f32,
    gun_cd: f32,
    charge_t: f32,
    reload_t: f32,
    lunge_left: f32,
    lunge_dir: (f32, f32),
    lunge_logged: u32,
    safe_pos: Option<(f32, f32, f32)>,
    safe_bid: Option<i32>,
    safe_t: f32,
    rescued: u32,
    l1_anim_start: Option<(f32, f32, f32)>,
    l1_anim_logged: u32,
    lunge_acc: (f32, f32),
    safe_hp: i32,
    prev_l1: bool,
    shots_logged: u32,
    diag_in: u32,

    test_fly: u8,
    test_fly_t: f32,
    test_fly_hp: i32,
    test_fire: u8,
    dbg_i: i32,
    dbg_t: f32,
    bullet_watch: u32,
    test_fire_t: f32,

    // self test: drop the character 25 m and log what the landing did to HP (armed by a file in the log folder)
    test_phase: u8,
    test_hp: i32,
    test_t: f32,

    // flight
    flying: bool,
    saved_gravity: Option<f32>,
    vy: f32,
    vel: (f32, f32),
    jump_armed: bool,
    fall_grace: f32,

    // stick -> world mapping, learned while walking on the ground
    map_scores: [f32; HYPS],
    map_samples: u32,
    map_best: usize,
    diag_logged: u32,

    // ground boost: dodge held time, current boost speed (m/s), and the slide after it
    dodge_held: f32,
    bv: f32,
    slide_dir: (f32, f32),
    slide_brake: f32,
    hold_logged: bool,
}

static STATE: Mutex<State> = Mutex::new(State {
    last_tick: None,
    frame_idx: None,
    heartbeat: 0,
    scaled_logged: false,
    notes_logged: 0,
    boost_left: 0.0,
    boost_total: 0.0,
    boost_frames: 0,
    boost_dir: None,
    boost_start: (0.0, 0.0, 0.0),
    boost_travelled: 0.0,
    boost_cost: 0.0,
    cooldown: 0.0,
    since_spend: 0.0,
    fp_carry: 0.0,
    en_carry_drain: 0.0,
    overheated: false,
    empty_timer: 0.0,
    last_pos: None,
    speed_logged: false,
    settle: 0.0,
    gravity_logged: false,
    last_anim: -1,
    anim_logged: 0,
    air_t: 0.0,
    block_ok_now: true,
    last_bid: i32::MIN,
    hp_logged: 0,
    ground_frames: 0,
    land_cool: 0.0,
    last_hp: 0,
    rounds: -1,
    back_rounds: -1,
    back_cd_r: 0.0,
    back_cd_l: 0.0,
    gun_cd: 0.0,
    charge_t: 0.0,
    reload_t: 0.0,
    lunge_left: 0.0,
    lunge_dir: (0.0, 0.0),
    lunge_logged: 0,
    safe_pos: None,
    safe_bid: None,
    safe_t: 0.0,
    rescued: 0,
    l1_anim_start: None,
    l1_anim_logged: 0,
    lunge_acc: (0.0, 0.0),
    safe_hp: 0,
    prev_l1: false,
    shots_logged: 0,
    diag_in: 0,
    test_fly: 0,
    test_fly_t: 0.0,
    test_fly_hp: 0,
    test_fire: 0,
    dbg_i: -1,
    dbg_t: 0.0,
    bullet_watch: 0,
    test_fire_t: 0.0,
    test_phase: 0,
    test_hp: 0,
    test_t: 0.0,
    flying: false,
    saved_gravity: None,
    vy: 0.0,
    vel: (0.0, 0.0),
    jump_armed: true,
    fall_grace: 0.0,
    map_scores: [0.0; HYPS],
    map_samples: 0,
    map_best: 0,
    diag_logged: 0,
    dodge_held: 0.0,
    bv: 0.0,
    slide_dir: (0.0, 0.0),
    slide_brake: 0.0,
    hold_logged: false,
});

// FP lives in two places (the character's data module and the player game data the HUD reads); keep both in step.
fn fp_now(player: &PlayerIns) -> (i32, i32) {
    let d = &player.chr_ins.modules.data;
    (d.fp, d.max_fp)
}

fn fp_set(player: &mut PlayerIns, value: i32) {
    let max = player.chr_ins.modules.data.max_fp;
    let v = value.clamp(0, max);
    player.chr_ins.modules.data.fp = v;
    unsafe { player.player_game_data.as_mut().current_fp = v as u32 };
}

/// Shortens a horizontal step so the frame stops short of walls: casts a ray from chest height along the step.
fn wall_step(player: &PlayerIns, px: f32, py: f32, pz: f32, dx: f32, dz: f32, step: f32) -> f32 {
    let Ok(havok) = (unsafe { CSHavokMan::instance() }) else { return step };
    let origin = HavokPosition(px, py + SIZE_SCALE * 0.9, pz, 0.0);
    let reach = step + WALL_MARGIN;
    match havok.phys_world.cast_ray(MAP_RAY_FILTER, &origin, PositionDelta(dx * reach, 0.0, dz * reach), player) {
        Some(hit) => {
            let hit_dist = ((hit.0 - origin.0).powi(2) + (hit.2 - origin.2).powi(2)).sqrt();
            (hit_dist - WALL_MARGIN).clamp(0.0, step)
        }
        None => step,
    }
}

/// Vertical clamp for flight: stops the climb below a ceiling and the descent above the ground.
fn vertical_step(player: &PlayerIns, px: f32, py: f32, pz: f32, dy: f32) -> f32 {
    let Ok(havok) = (unsafe { CSHavokMan::instance() }) else { return dy };
    if dy > 0.0 {
        let origin = HavokPosition(px, py + SIZE_SCALE * 1.8, pz, 0.0);
        if havok.phys_world.cast_ray(MAP_RAY_FILTER, &origin, PositionDelta(0.0, dy + 0.5, 0.0), player).is_some() {
            return 0.0;
        }
    } else if dy < 0.0 {
        let origin = HavokPosition(px, py + 0.4, pz, 0.0);
        if let Some(hit) = havok.phys_world.cast_ray(MAP_RAY_FILTER, &origin, PositionDelta(0.0, dy - 0.4, 0.0), player) {
            return (hit.1 - py).min(0.0).max(dy);
        }
    }
    dy
}

/// Height of the map surface under (x, z), searched from `y + 3` down 8 m (None if there is none).
fn ground_top(player: &PlayerIns, x: f32, y: f32, z: f32) -> Option<f32> {
    let havok = (unsafe { CSHavokMan::instance() }).ok()?;
    let origin = HavokPosition(x, y + 3.0, z, 0.0);
    let hit = havok.phys_world.cast_ray(MAP_RAY_FILTER, &origin, PositionDelta(0.0, -8.0, 0.0), player)?;
    Some(hit.1)
}

/// Distance from the frame's feet to the map below (None if nothing within reach). The game's own 'standing' flag lies
/// while we hold gravity off, so landing is judged with this.
fn ground_gap(player: &PlayerIns, px: f32, py: f32, pz: f32, reach: f32) -> Option<f32> {
    let havok = (unsafe { CSHavokMan::instance() }).ok()?;
    let origin = HavokPosition(px, py + 0.5, pz, 0.0);
    let hit = havok.phys_world.cast_ray(MAP_RAY_FILTER, &origin, PositionDelta(0.0, -(reach + 0.5), 0.0), player)?;
    Some(py - hit.1)
}

struct Input {
    jump: bool,
    dodge: bool,
    r1: bool,
    r2: bool,
    l1: bool,
    l2: bool,
    fwd: f32,
    right: f32,
}

impl Input {
    fn mag(&self) -> f32 {
        (self.fwd * self.fwd + self.right * self.right).sqrt().min(1.0)
    }
}

/// Pad (or keyboard) state; None while menus own the pad. A stick reports an analog value; keyboard keys only
/// report "down", which counts as full deflection.
fn read_input() -> Option<Input> {
    let pads = unsafe { FD4PadManager::instance() }.ok()?;
    let pad = pads.get_in_game_pad()?;
    let axis = |plus: UserInputKey, minus: UserInputKey| {
        let analog = pad.poll_analog_input(plus) - pad.poll_analog_input(minus);
        if analog.abs() > 0.05 {
            analog.clamp(-1.0, 1.0)
        } else {
            (pad.poll_digital_input(plus) as i32 - pad.poll_digital_input(minus) as i32) as f32
        }
    };
    Some(Input {
        jump: pad.poll_digital_input(UserInputKey::Jump),
        dodge: pad.poll_digital_input(UserInputKey::Backstep),
        r1: pad.poll_digital_input(UserInputKey::Attack),
        r2: pad.poll_digital_input(UserInputKey::StrongAttack),
        l1: pad.poll_digital_input(UserInputKey::Guard),
        l2: pad.poll_digital_input(UserInputKey::Skill),
        fwd: axis(UserInputKey::MoveForwards, UserInputKey::MoveBackwards),
        right: axis(UserInputKey::MoveRight, UserInputKey::MoveLeft),
    })
}

struct Cam {
    /// Ground-plane forward from the camera matrix's third row (unverified convention; the mapper checks it).
    row_fwd: Option<(f32, f32)>,
    /// Camera position on the ground plane (matrix translation row).
    pos: Option<(f32, f32)>,
    /// Camera position in 3D (for aiming).
    pos3: (f32, f32, f32),
}

fn norm2(x: f32, z: f32) -> Option<(f32, f32)> {
    let l = (x * x + z * z).sqrt();
    if l < 1e-4 || !l.is_finite() { None } else { Some((x / l, z / l)) }
}

fn read_camera() -> Option<Cam> {
    let cam = unsafe { CSCamera::instance() }.ok()?;
    let m = &cam.pers_cam_1.matrix;
    Some(Cam { row_fwd: norm2(m.2.0, m.2.2), pos: Some((m.3.0, m.3.2)), pos3: (m.3.0, m.3.1, m.3.2) })
}

/// Heading (ground-plane unit vector) from hypothesis source 0 (matrix row) or 1 (from the camera to the player).
fn heading(src: usize, cam: &Cam, pos: (f32, f32)) -> Option<(f32, f32)> {
    if src == 0 {
        cam.row_fwd
    } else {
        let (cx, cz) = cam.pos?;
        let (dx, dz) = (pos.0 - cx, pos.1 - cz);
        let d = (dx * dx + dz * dz).sqrt();
        if !(0.5..60.0).contains(&d) { None } else { norm2(dx, dz) }
    }
}

/// One of the 8 ways (sign of each axis, swapped or not) the stick can map onto heading and its perpendicular.
fn combine(k: usize, h: (f32, f32), fwd: f32, right: f32) -> (f32, f32) {
    let p = (-h.1, h.0);
    let f = if k & 1 != 0 { -fwd } else { fwd };
    let r = if k & 2 != 0 { -right } else { right };
    let (c1, c2) = if k & 4 != 0 { (p, h) } else { (h, p) };
    (c1.0 * f + c2.0 * r, c1.1 * f + c2.1 * r)
}

/// World direction (x, z) for the stick under the learned mapping. With no stick and `or_forward`, straight ahead of the camera.
fn stick_world(st: &State, inp: &Input, cam: &Cam, pos: (f32, f32), or_forward: bool) -> Option<(f32, f32, f32)> {
    let (src, k) = (st.map_best / 8, st.map_best % 8);
    let h = heading(src, cam, pos).or_else(|| heading(1 - src, cam, pos))?;
    let mag = inp.mag();
    if mag < 0.1 {
        if or_forward {
            let (x, z) = combine(k, h, 1.0, 0.0);
            return norm2(x, z).map(|(x, z)| (x, z, 1.0));
        }
        return None;
    }
    let (x, z) = combine(k, h, inp.fwd, inp.right);
    norm2(x, z).map(|(x, z)| (x, z, mag))
}

/// Score every hypothesis against one observed ground step (the game moved the player along `obs` while the
/// stick said `inp`); after a few samples the best one is used for steering in the air.
fn learn(st: &mut State, inp: &Input, cam: &Cam, pos: (f32, f32), obs: (f32, f32)) {
    for hyp in 0..HYPS {
        let (src, k) = (hyp / 8, hyp % 8);
        let Some(h) = heading(src, cam, pos) else { continue };
        let (x, z) = combine(k, h, inp.fwd, inp.right);
        let Some((px, pz)) = norm2(x, z) else { continue };
        st.map_scores[hyp] = st.map_scores[hyp] * 0.998 + (px * obs.0 + pz * obs.1);
    }
    st.map_samples += 1;
    if st.diag_logged < 10 && st.map_samples % 7 == 0 {
        st.diag_logged += 1;
        log(&format!(
            "mech: input sample fwd {:.2} right {:.2}, camrow {:?}, campos {:?}, player {:.1},{:.1}, moved {:.2},{:.2}",
            inp.fwd, inp.right, cam.row_fwd, cam.pos, pos.0, pos.1, obs.0, obs.1
        ));
    }
    if st.map_samples >= 8 {
        let mut best = st.map_best;
        for hyp in 0..HYPS {
            if st.map_scores[hyp] > st.map_scores[best] + 1.0 {
                best = hyp;
            }
        }
        if best != st.map_best {
            st.map_best = best;
            log(&format!("mech: stick mapping -> source {} arrangement {} (score {:.1}, {} samples)", best / 8, best % 8, st.map_scores[best], st.map_samples));
        }
    }
}

fn move_toward(v: f32, target: f32, max_delta: f32) -> f32 {
    if (target - v).abs() <= max_delta { target } else { v + (target - v).signum() * max_delta }
}

fn note(st: &mut State, msg: &str) {
    if st.notes_logged < 150 {
        st.notes_logged += 1;
        log(msg);
    }
}

/// Spawns one bullet from the game's own bullet manager (the Bullet row carries speed, range, radius, effects and damage).
fn spawn_shot(player: &PlayerIns, bullet_id: i32, origin: (f32, f32, f32), dir: (f32, f32, f32)) -> Result<(), i32> {
    const _: () = assert!(std::mem::size_of::<BulletSpawnData>() == 0x110);
    let Ok(mgr) = (unsafe { CSBulletManager::instance_mut() }) else { return Err(-100) };
    #[repr(C, align(16))]
    struct Raw([u8; 0x110]);
    let mut raw = Raw([0; 0x110]);
    let b = &mut raw.0;
    fn w32(b: &mut [u8], off: usize, v: i32) {
        b[off..off + 4].copy_from_slice(&v.to_le_bytes());
    }
    fn wf(b: &mut [u8], off: usize, v: f32) {
        b[off..off + 4].copy_from_slice(&v.to_le_bytes());
    }
    let handle = player.chr_ins.field_ins_handle;
    unsafe { std::ptr::copy_nonoverlapping(&handle as *const _ as *const u8, b.as_mut_ptr(), 8) };
    w32(b, 0x08, -1); // behavior id
    w32(b, 0x0c, -1); // magic id
    w32(b, 0x14, bullet_id);
    w32(b, 0x18, -1); // goods id
    w32(b, 0x1c, -1); // dummy poly
    for i in 0x20..0x28 {
        b[i] = 0xFF; // no target override
    }
    for base in [0x50usize, 0x70] {
        wf(b, base, dir.0);
        wf(b, base + 4, dir.1);
        wf(b, base + 8, dir.2);
    }
    wf(b, 0x80, origin.0);
    wf(b, 0x84, origin.1);
    wf(b, 0x88, origin.2);
    wf(b, 0x8c, 1.0);
    let data = unsafe { &*(b.as_ptr() as *const BulletSpawnData) };
    mgr.spawn_bullet(data)
}

fn drain_en(player: &mut PlayerIns, st: &mut State, pct_per_s: f32, max_fp: f32, dt: f32) {
    st.en_carry_drain += pct_per_s / 100.0 * max_fp * dt;
    st.since_spend = 0.0;
    if st.en_carry_drain >= 1.0 {
        let whole = st.en_carry_drain.floor();
        st.en_carry_drain -= whole;
        let (cur, _) = fp_now(player);
        fp_set(player, cur - whole as i32);
    }
}

fn tick(_data: &FD4TaskData) {
    let Ok(mut st) = STATE.lock() else { return };
    let now = Instant::now();
    let dt = st.last_tick.map(|t| (now - t).as_secs_f32()).unwrap_or(0.016).clamp(0.001, 0.05);
    st.last_tick = Some(now);
    st.heartbeat += 1;

    let Ok(world) = (unsafe { WorldChrMan::instance_mut() }) else { return };
    let Some(player) = world.main_player.as_mut() else { return };

    // ---- only act on a real character (the title screen also has a "player", and menu buttons look like dodges) ----
    let (has_character, archetype, level) = {
        let pgd = unsafe { player.player_game_data.as_ref() };
        (pgd.character_name[0] != 0 && pgd.level > 0, pgd.archetype, pgd.level)
    };
    if !has_character {
        st.frame_idx = None;
        st.boost_left = 0.0;
        st.flying = false;
        st.saved_gravity = None;
        return;
    }

    // ---- which frame did this character start as? (starting class 0 Vagabond, 1 Warrior, 2 Hero -> rows 3000..) ----
    if st.frame_idx.is_none() {
        let class_row = 3000 + archetype as i32;
        let idx = FRAMES.iter().position(|f| f.class_row == class_row).unwrap_or(1);
        st.frame_idx = Some(idx);
        let f = &FRAMES[idx];
        log(&format!(
            "mech: character level {level}, starting class {archetype} (row {class_row}) -> {} (QB {:.1} m, boost {:.1}/{:.1} m/s, vboost {:.1} m/s, gravity {:.0})",
            f.id, f.qb_distance_m, f.ground_boost_mps, f.air_boost_mps, f.vboost_up_mps, f.gravity_mps2
        ));
    }
    let frame: &Frame = &FRAMES[st.frame_idx.unwrap_or(1)];

    // ---- size ----
    {
        let ctrl = &mut player.chr_ins.chr_ctrl;
        ctrl.scale_size_x = SIZE_SCALE;
        ctrl.scale_size_y = SIZE_SCALE;
        ctrl.scale_size_z = SIZE_SCALE;
    }
    if !st.scaled_logged {
        st.scaled_logged = true;
        log(&format!("mech: scale set to {SIZE_SCALE}"));
    }
    // ---- no fall damage, ever (AC6 frames do not take it): the fall clock never gets far enough to hurt, and the
    // game's own 'no fall damage here' flag is forced on every frame ----
    {
        let m = &mut player.chr_ins.modules;
        if m.fall.fall_timer > 0.2 {
            m.fall.fall_timer = 0.2;
        }
        m.material.disable_fall_damage = true;
    }
    // heartbeat for diagnosing tests
    if st.heartbeat % 600 == 1 {
        let b = player.chr_ins.block_id;
        let (fp, mx) = fp_now(player);
        let ph = &player.chr_ins.modules.physics;
        log(&format!("mech: tick {}, player block {b:?}, EN {fp}/{mx}, flying {}, overheated {}, mapping {}, ground {} falling {} grav {} gdis {} settle {:.0}", st.heartbeat, st.flying, st.overheated, st.map_best, ph.standing_on_solid_ground, ph.is_falling, ph.gravity_multiplier, ph.gravity_disabled, st.settle));
    }

    {
        // which animation is the game playing right now (a000_<id>): used to map AC6 animations onto the right Elden Ring slots
        let (anim_id, anim_len) = {
            let ta = &player.chr_ins.modules.time_act;
            let a = &ta.anim_queue[(ta.read_idx as usize) % 10];
            (a.anim_id, a.anim_length)
        };
        let l1_anim = (29_000_000..30_000_000).contains(&anim_id);
        if l1_anim {
            // the game's own L1 combo (a000_29035xxx) moves the character sideways with its animation; the lunge is ours
            let pp = &player.chr_ins.modules.physics.position;
            if st.l1_anim_start.is_none() {
                st.l1_anim_start = Some((pp.0, pp.1, pp.2));
                st.lunge_acc = (0.0, 0.0);
            }
            player.chr_ins.modules.behavior.root_motion = F32Vector4(0.0, 0.0, 0.0, 0.0);
        } else if let Some((sx, sy, sz)) = st.l1_anim_start.take() {
            let pp = &player.chr_ins.modules.physics.position;
            if st.l1_anim_logged < 8 {
                st.l1_anim_logged += 1;
                log(&format!("mech: L1 animation ended, moved ({:.2},{:.2},{:.2}) m, of which our lunges ({:.2},{:.2})", pp.0 - sx, pp.1 - sy, pp.2 - sz, st.lunge_acc.0, st.lunge_acc.1));
            }
        }
        if anim_id != st.last_anim {
            st.last_anim = anim_id;
            if st.anim_logged < 800 {
                st.anim_logged += 1;
                log(&format!("anim: {} (length {:.2} s)", anim_id, anim_len));
            }
        }
    }
    let (fp, max_fp_i) = fp_now(player);
    let max_fp = max_fp_i.max(1) as f32;
    let pressed = player.chr_ins.modules.action_request.new_action_presses.sp_move();
    let (px, py, pz) = {
        let p = &player.chr_ins.modules.physics.position;
        (p.0, p.1, p.2)
    };
    let (on_ground_flag, falling_flag) = {
        let ph = &player.chr_ins.modules.physics;
        (ph.standing_on_solid_ground, ph.is_falling)
    };
    let grounded = on_ground_flag && !falling_flag;
    let input = read_input();
    let have_input = input.is_some();
    let mut inp = input.unwrap_or(Input { jump: false, dodge: false, r1: false, r2: false, l1: false, l2: false, fwd: 0.0, right: 0.0 });
    // self test: a long boosted flight (EN kept full) that crosses map tiles and then lets go; logs HP before and after
    {
        let flag = crate::log_dir().join("selftest_fly");
        if st.test_fly == 0 && st.settle > 5.0 && grounded && flag.exists() {
            let _ = std::fs::remove_file(&flag);
            st.test_fly = 1;
            st.test_fly_t = 0.0;
            st.test_fly_hp = player.chr_ins.modules.data.hp;
            // our synthetic jump cannot make the game jump: lift the frame off the ground so the takeoff rule sees it airborne
            let phys = &mut player.chr_ins.modules.physics;
            phys.position.1 += 1.5;
            phys.chr_proxy_pos_update_requested = true;
            log(&format!("selftest: long flight started, HP {}", st.test_fly_hp));
        }
        if st.test_fly == 1 {
            st.test_fly_t += dt;
            let t = st.test_fly_t;
            inp.jump = t < 2.0;
            inp.dodge = t > 0.4 && t < 16.0;
            let max = player.chr_ins.modules.data.max_fp;
            fp_set(player, max);
            if t > 16.0 {
                st.test_fly = 2;
                st.test_fly_t = 0.0;
                log("selftest: long flight input released");
            }
        } else if st.test_fly == 2 {
            st.test_fly_t += dt;
            if st.test_fly_t > 12.0 && grounded {
                let hp = player.chr_ins.modules.data.hp;
                log(&format!("selftest: FLY RESULT HP before {} after {} (lost {})", st.test_fly_hp, hp, st.test_fly_hp - hp));
                st.test_fly = 0;
            }
        }
    }
    let cam = read_camera().unwrap_or(Cam { row_fwd: None, pos: None, pos3: (0.0, 0.0, 0.0) });
    let pos2 = (px, pz);
    // a loading screen, a warp or a respawn resets the settle clock; flight only starts once things have been quiet
    {
        let bid = player.chr_ins.block_id;
        // area 255 = loading; area 10 blocks 0/1 = the scripted tutorial maps (Stranded Graveyard, Chapel of Anticipation)
        let block_ok = bid.area() != 255 && !(bid.area() == 10 && bid.block() <= 1);
        // the overworld is cut into tiles and the player's position is relative to its tile: crossing a tile edge changes the
        // block id and jumps the coordinates by a whole tile. That is NOT a warp; keep flying, just forget the old position.
        let bid_i = i32::from(bid);
        let tile_changed = st.last_bid != bid_i;
        st.last_bid = bid_i;
        if tile_changed && block_ok {
            st.last_pos = None;
            if st.flying {
                note(&mut st, &format!("mech: tile crossed while flying -> block {} (flight kept)", bid_i));
            }
        }
        let teleported = !tile_changed && st.last_pos.map(|(lx, _, lz)| ((px - lx).powi(2) + (pz - lz).powi(2)).sqrt() > 20.0).unwrap_or(false);
        st.block_ok_now = block_ok;
        if !block_ok || teleported {
            st.settle = 0.0;
            // The block id also reads "loading" (255) for a moment while the frame crosses map tiles, so a flight is NOT
            // dropped for that: letting the game's own gravity take over in mid-air builds a lethal fall. Only the scripted
            // tutorial maps end a flight (the landing ray ends all others).
            if st.flying && bid.area() == 10 && bid.block() <= 1 {
                st.flying = false;
                let m = &mut player.chr_ins.modules;
                m.physics.gravity_multiplier = 1.0;
                m.physics.gravity_disabled = false;
                st.saved_gravity = None;
                note(&mut st, "mech: flight ended (tutorial map)");
            }
        } else {
            st.settle += dt;
        }
    }
    if !inp.jump {
        st.jump_armed = true;
    }

    // ---- self test: fall damage ----
    {
        let flag = crate::log_dir().join("selftest_fall");
        let hp_now = player.chr_ins.modules.data.hp;
        match st.test_phase {
            0 => {
                if st.settle > 6.0 && grounded && flag.exists() {
                    let height = std::fs::read_to_string(&flag).ok().and_then(|t| t.trim().parse::<f32>().ok()).unwrap_or(25.0);
                    let _ = std::fs::remove_file(&flag);
                    st.test_hp = hp_now;
                    let phys = &mut player.chr_ins.modules.physics;
                    phys.position.1 += height;
                    log(&format!("selftest: drop height {height} m"));
                    phys.chr_proxy_pos_update_requested = true;
                    st.test_phase = 1;
                    st.test_t = 0.0;
                    log(&format!("selftest: dropping from +25 m, HP {hp_now}"));
                }
            }
            1 => {
                st.test_t += dt;
                // dump the fall module raw (its fields past the timer are unnamed) and the HP, every ~0.1 s
                if (st.heartbeat % 6) == 0 {
                    let fm = &*player.chr_ins.modules.fall as *const _ as *const u8;
                    let mut raw = String::new();
                    for i in 0..0x30usize {
                        raw.push_str(&format!("{:02x}", unsafe { *fm.add(i) }));
                    }
                    let ph = &player.chr_ins.modules.physics;
                    log(&format!("selftest: t {:.2} y {:.2} hp {} timer {:.2} falling {} ground {} fall raw {}", st.test_t, ph.position.1, hp_now, player.chr_ins.modules.fall.fall_timer, ph.is_falling, ph.standing_on_solid_ground, raw));
                }
                if st.test_t > 0.5 && grounded {
                    st.test_phase = 2;
                    st.test_t = 0.0;
                    log(&format!("selftest: landed after {:.1} s, HP {hp_now}/{}", st.test_t, st.test_hp));
                } else if st.test_t > 15.0 {
                    st.test_phase = 0;
                    log("selftest: never landed (gave up)");
                }
            }
            _ => {
                st.test_t += dt;
                if st.test_t > 2.5 {
                    log(&format!("selftest: RESULT HP before {} after {} (lost {})", st.test_hp, hp_now, st.test_hp - hp_now));
                    st.test_phase = 0;
                }
            }
        }
    }

    // ---- overheat: EN hit zero while boosting/thrusting; the generator's empty delay must pass before EN returns ----
    if fp <= 0 && !st.overheated && (st.flying || st.bv > 0.5 || st.boost_left > 0.0) {
        st.overheated = true;
        st.empty_timer = frame.en_empty_delay_s;
        note(&mut st, &format!("mech: EN empty, overheated for {:.1} s", frame.en_empty_delay_s));
    }
    if st.overheated {
        st.empty_timer -= dt;
        if st.empty_timer <= 0.0 {
            st.overheated = false;
            let restore = (frame.en_empty_restore_pct / 100.0 * max_fp).ceil() as i32;
            let (cur, _) = fp_now(player);
            fp_set(player, cur.max(restore));
            note(&mut st, "mech: EN back");
        }
    }
    let can_thrust = fp > 0 && !st.overheated;

    // ---- boost: dodge held past the Quick Boost dash ----
    st.dodge_held = if inp.dodge { st.dodge_held + dt } else { 0.0 };
    let stick_mag = inp.mag();
    let want_hold = st.dodge_held >= HOLD_AFTER
        && can_thrust
        && st.boost_left <= 0.0
        && ((grounded && stick_mag > 0.1) || st.flying);
    if want_hold {
        drain_en(player, &mut st, frame.boost_en_pct_per_s, max_fp, dt);
        if !st.hold_logged {
            st.hold_logged = true;
            log(&format!("mech: boost hold active (ground {:.1} m/s, air {:.1} m/s, {:.1}% EN/s)", frame.ground_boost_mps, frame.air_boost_mps, frame.boost_en_pct_per_s));
        }
    }

    // ---- ground movement: normal speed, boost, and the slide after a boost ----
    // The game moves the player (walk/run/sprint animations); we add the difference up to our speed. Only while grounded,
    // never across a teleport, and stopped by walls. Also teaches us how the stick maps to the world.
    let norm_speed = SIZE_SCALE * frame.move_speed_scale;
    if grounded && st.boost_left <= 0.0 && !st.flying {
        if st.slide_brake <= 0.0 {
            st.slide_brake = frame.ground_brake_mps2;
        }
        if want_hold {
            st.bv = (st.bv + frame.ground_boost_accel * dt).min(frame.ground_boost_mps);
        } else {
            st.bv = (st.bv - st.slide_brake * dt).max(0.0);
        }
        if let Some((lx, _ly, lz)) = st.last_pos {
            let (dx, dz) = (px - lx, pz - lz);
            let len = (dx * dx + dz * dz).sqrt();
            if len > 0.0005 && len < 1.0 {
                let (ux, uz) = (dx / len, dz / len);
                if have_input && stick_mag > 0.5 {
                    learn(&mut st, &inp, &cam, pos2, (ux, uz));
                }
                // total step this frame: the larger of the normal-speed step and the boost speed
                let total = (len * norm_speed).max(st.bv * dt);
                let extra = wall_step(player, px, py, pz, ux, uz, (total - len).max(0.0));
                st.slide_dir = (ux, uz);
                if extra > 0.0 {
                    let phys = &mut player.chr_ins.modules.physics;
                    phys.position.0 += ux * extra;
                    phys.position.2 += uz * extra;
                    phys.chr_proxy_pos_update_requested = true;
                    if !st.speed_logged {
                        st.speed_logged = true;
                        log(&format!("mech: ground speed x{norm_speed:.2} active (first extra step {extra:.3} m)"));
                    }
                }
            } else if len <= 0.0005 && st.bv > 0.4 && !want_hold {
                // the game has stopped moving us after a boost: keep sliding on the momentum, braking
                let (ux, uz) = st.slide_dir;
                let step = wall_step(player, px, py, pz, ux, uz, st.bv * dt);
                if step > 0.0001 {
                    let phys = &mut player.chr_ins.modules.physics;
                    phys.position.0 += ux * step;
                    phys.position.2 += uz * step;
                    phys.chr_proxy_pos_update_requested = true;
                } else {
                    st.bv = 0.0;
                }
            }
        }
        if st.bv <= 0.0 {
            st.slide_brake = frame.ground_brake_mps2;
        }
    }

    // ---- airtime (feeds the automatic AC6 descent) and the landing HP guard ----
    if !grounded && !on_ground_flag {
        st.air_t += dt;
    } else {
        st.air_t = 0.0;
    }
    st.ground_frames = if on_ground_flag { st.ground_frames + 1 } else { 0 };
    st.land_cool = (st.land_cool - dt).max(0.0);
    {
        let hp = player.chr_ins.modules.data.hp;
        if st.last_hp > 0 && hp != st.last_hp && st.hp_logged < 60 && st.fall_grace <= 0.0 {
            st.hp_logged += 1;
            log(&format!("mech: player HP {} -> {} (flying {}, air {:.2} s, y {:.1}, vy {:.1})", st.last_hp, hp, st.flying, st.air_t, py, st.vy));
        }
        // right after a flight/fall ends the landing must not cost HP: put back anything lost in that window
        if st.fall_grace > 0.0 && st.last_hp > 0 && hp < st.last_hp && hp >= 0 {
            player.chr_ins.modules.data.hp = st.last_hp;
            let lost = st.last_hp - hp;
            note(&mut st, &format!("mech: landing damage undone ({lost} HP)"));
        } else {
            st.last_hp = hp;
        }
    }

    // ---- never lost under the map ----
    // Remember the last firm ground (same map tile). If the mech is far below it with no ground anywhere under it, the capsule
    // has gone through the terrain (a lunge into a slope, a load glitch): put it back instead of falling forever.
    {
        let bid = player.chr_ins.block_id.0;
        st.safe_t -= dt;
        // self test: a flag file drops the mech 15 m through the ground to prove the rescue
        {
            let flag = crate::log_dir().join("selftest_under");
            if grounded && !st.flying && st.settle > 5.0 && st.safe_pos.is_some() && flag.exists() {
                let _ = std::fs::remove_file(&flag);
                let phys = &mut player.chr_ins.modules.physics;
                phys.position.1 -= 15.0;
                phys.chr_proxy_pos_update_requested = true;
                log("selftest: dropped 15 m through the ground");
            }
        }
        if grounded && !st.flying && st.settle > 2.0 && st.block_ok_now && st.safe_t <= 0.0 {
            if let Some(g) = ground_gap(player, px, py, pz, 1.0) {
                if g < 0.3 {
                    st.safe_pos = Some((px, py, pz));
                    st.safe_bid = Some(bid);
                    st.safe_t = 0.5;
                    st.safe_hp = player.chr_ins.modules.data.hp;
                }
            }
        }
        if let (Some((sx, sy, sz)), Some(sb)) = (st.safe_pos, st.safe_bid) {
            if sb == bid && py < sy - 8.0 && ground_gap(player, px, py, pz, 150.0).is_none() {
                st.rescued += 1;
                let phys = &mut player.chr_ins.modules.physics;
                phys.position.0 = sx;
                phys.position.1 = sy + 0.5;
                phys.position.2 = sz;
                phys.chr_proxy_pos_update_requested = true;
                if st.flying {
                    st.flying = false;
                    phys.gravity_multiplier = st.saved_gravity.take().unwrap_or(1.0);
                    phys.gravity_disabled = false;
                }
                st.vy = 0.0;
                st.vel = (0.0, 0.0);
                st.bv = 0.0;
                st.lunge_left = 0.0;
                st.fall_grace = 4.0;
                st.land_cool = 0.6;
                if st.safe_hp > 0 && player.chr_ins.modules.data.hp < st.safe_hp {
                    player.chr_ins.modules.data.hp = st.safe_hp;
                    st.last_hp = st.safe_hp;
                }
                let n = st.rescued;
                note(&mut st, &format!("mech: fell under the map (y {py:.1}, safe y {sy:.1}): put back on the last firm ground (rescue {n})"));
            }
        }
    }

    // ---- ground boost hugs the ground ----
    // Skimming over bumps at boost speed leaves the ground for a few frames; the game then plays its jump / fall / landing
    // clips over and over. AC6 hovers along the ground, so while boosting on foot a small gap is closed at once.
    if !st.flying && !grounded && !inp.jump && st.bv > 1.0 && st.dodge_held >= HOLD_AFTER && st.boost_left <= 0.0 {
        if let Some(g) = ground_gap(player, px, py, pz, 2.0) {
            if g > 0.02 && g < 2.0 {
                let phys = &mut player.chr_ins.modules.physics;
                phys.position.1 -= g;
                phys.chr_proxy_pos_update_requested = true;
                st.air_t = 0.0;
            }
        }
    }

    // ---- flight ----
    if !st.flying {
        // take off: airborne, jump held (and not still held from before), some EN left
        let takeoff = st.settle > 2.0 && !grounded && !on_ground_flag && inp.jump && st.jump_armed && can_thrust && st.boost_left <= 0.0;
        // any real fall (a ledge, a jump coming down) is taken over by the AC6 descent: the game never builds up a fall
        let auto_fall = st.block_ok_now && st.air_t > 0.25 && st.land_cool <= 0.0 && st.boost_left <= 0.0;
        if takeoff || auto_fall {
            st.flying = true;
            // carry on with the speed the game had, so nothing jerks
            let (evx, evy, evz) = match st.last_pos {
                Some((lx, ly, lz)) => ((px - lx) / dt, (py - ly) / dt, (pz - lz) / dt),
                None => (0.0, 0.0, 0.0),
            };
            st.vy = evy.clamp(-60.0, 30.0);
            let hs = (evx * evx + evz * evz).sqrt();
            let k = if hs > 30.0 { 30.0 / hs } else { 1.0 };
            st.vel = (evx * k, evz * k);
            let ph = &mut player.chr_ins.modules.physics;
            let g = ph.gravity_multiplier;
            st.saved_gravity = Some(if (0.5..=3.0).contains(&g) { g } else { 1.0 });
            if !st.gravity_logged {
                st.gravity_logged = true;
                log(&format!("mech: gravity multiplier at takeoff {g}, gravity_disabled {}", ph.gravity_disabled));
            }
            let msg = format!("mech: flight on (EN {fp}/{max_fp_i}, mapping {}, input {})", st.map_best, if have_input { "ok" } else { "MISSING" });
            note(&mut st, &msg);
        }
    }
    if st.flying {
        {
            // gravity off (we apply the AC6 value ourselves), falling clock and damage off
            let m = &mut player.chr_ins.modules;
            m.physics.gravity_multiplier = 0.0;
            m.physics.gravity_disabled = true;
            m.fall.fall_timer = 0.0;
            m.material.disable_fall_damage = true;
        }
        let thrusting = inp.jump && can_thrust;
        if thrusting {
            drain_en(player, &mut st, frame.vboost_en_pct_per_s, max_fp, dt);
        }
        // vertical: vertical boost climbs; otherwise gravity (plus the fly-up brake while still rising)
        st.vy = if thrusting {
            move_toward(st.vy, frame.vboost_up_mps, frame.vboost_up_accel * dt)
        } else {
            let brake = if st.vy > 0.0 { frame.fly_up_brake_mps2 } else { 0.0 };
            (st.vy - (frame.gravity_mps2 + brake) * dt).max(-frame.fall_max_mps)
        };

        // horizontal: boost speed with boost held, slow vertical-boost speed while climbing, jump air control otherwise
        let want = stick_world(&st, &inp, &cam, pos2, want_hold);
        let (target, accel) = if want_hold {
            (frame.air_boost_mps, frame.air_boost_accel)
        } else if thrusting {
            (frame.vboost_h_mps, frame.vboost_h_accel)
        } else {
            (frame.air_h_mps, frame.air_h_accel)
        };
        let (tx, tz) = match want {
            Some((ux, uz, mag)) => {
                let m = if want_hold { 1.0 } else { mag };
                (ux * target * m, uz * target * m)
            }
            None => (0.0, 0.0),
        };
        {
            let (vx, vz) = st.vel;
            let cur = (vx * vx + vz * vz).sqrt();
            let tgt = (tx * tx + tz * tz).sqrt();
            // speeding up uses the accel; slowing down (stick released, or target lower than we are going) the fly brake
            let rate = if tgt >= cur { accel } else { frame.fly_h_brake_mps2 };
            let (ex, ez) = (tx - vx, tz - vz);
            let d = (ex * ex + ez * ez).sqrt();
            let maxd = rate * dt;
            st.vel = if d <= maxd || d < 1e-4 { (tx, tz) } else { (vx + ex / d * maxd, vz + ez / d * maxd) };
        }
        let speed_h = (st.vel.0 * st.vel.0 + st.vel.1 * st.vel.1).sqrt();
        let (mut dx, mut dz) = (0.0, 0.0);
        if speed_h > 0.05 {
            let (ux, uz) = (st.vel.0 / speed_h, st.vel.1 / speed_h);
            let want_step = speed_h * dt;
            let step = wall_step(player, px, py, pz, ux, uz, want_step);
            if step < want_step * 0.95 {
                st.vel = (0.0, 0.0); // hit a wall: lose the momentum
            }
            dx = ux * step;
            dz = uz * step;
        }
        let dy = vertical_step(player, px, py, pz, st.vy * dt);
        if dy.abs() < (st.vy * dt).abs() * 0.5 {
            st.vy = 0.0; // ceiling or floor stopped us
        }
        {
            let phys = &mut player.chr_ins.modules.physics;
            phys.position.0 += dx;
            phys.position.1 += dy;
            phys.position.2 += dz;
            phys.chr_proxy_pos_update_requested = true;
        }
        // landing: the capsule is standing on something and we are not climbing
        let gap = ground_gap(player, px, py, pz, 1.0 + (st.vy * dt).abs());
        let touched = gap.map(|g| g <= 0.15).unwrap_or(false) || (st.vy.abs() < 0.01 && on_ground_flag && st.ground_frames >= 3);
        if touched && st.vy <= 0.0 && !thrusting {
            st.flying = false;
            st.fall_grace = FALL_GRACE;
            st.land_cool = 0.4;
            // skid on the way in: the ground slide carries the horizontal momentum, braking at the landing brake
            if speed_h > 1.0 {
                st.slide_dir = (st.vel.0 / speed_h, st.vel.1 / speed_h);
                st.bv = speed_h;
                st.slide_brake = frame.landing_brake_mps2;
            }
            st.vel = (0.0, 0.0);
            let m = &mut player.chr_ins.modules;
            m.physics.gravity_multiplier = st.saved_gravity.take().unwrap_or(1.0);
            m.physics.gravity_disabled = false;
            let (ly, vy) = (py, st.vy);
            note(&mut st, &format!("mech: flight ended (landed) y {ly:.2} vy {vy:.1} speed {speed_h:.1}"));
        }
    } else if st.fall_grace > 0.0 {
        st.fall_grace -= dt;
        let m = &mut player.chr_ins.modules;
        m.fall.fall_timer = 0.0;
        m.material.disable_fall_damage = st.fall_grace > 0.0;
    }

    // ---- weapons: the gun is fired by the mod (AC6 cycle time, ammo, charge, projectile numbers from the sheet) ----
    {
        if st.rounds < 0 {
            st.rounds = frame.gun_total_rounds;
        }
        st.gun_cd = (st.gun_cd - dt).max(0.0);
        if st.bullet_watch > 0 {
            st.bullet_watch -= 1;
            if st.bullet_watch % 10 == 0 {
                if let Ok(mgr) = unsafe { CSBulletManager::instance_mut() } {
                    let mut n = 0;
                    let mut lines = String::new();
                    for b in mgr.bullets() {
                        n += 1;
                        if n <= 3 {
                            let p = &b.physics.position;
                            lines.push_str(&format!(" [{:.1},{:.1},{:.1} alive {:.2}]", p.0, p.1, p.2, b.time_alive));
                        }
                    }
                    log(&format!("mech: bullets in world {n}{lines}"));
                }
            }
        }
        // aim: from the camera through the frame's head; falls back to the camera heading
        let head = (px, py + AIM_HEIGHT_M * 0.9, pz);
        let d3 = (head.0 - cam.pos3.0, head.1 - cam.pos3.1, head.2 - cam.pos3.2);
        let dl = (d3.0 * d3.0 + d3.1 * d3.1 + d3.2 * d3.2).sqrt();
        let aim = if dl > 1.0 && dl < 60.0 {
            (d3.0 / dl, d3.1 / dl, d3.2 / dl)
        } else {
            let none = Input { jump: false, dodge: false, r1: false, r2: false, l1: false, l2: false, fwd: 0.0, right: 0.0 };
            stick_world(&st, &none, &cam, pos2, true).map(|(x, z, _)| (x, 0.0, z)).unwrap_or((0.0, 0.0, -1.0))
        };
        let hl = (aim.0 * aim.0 + aim.2 * aim.2).sqrt().max(1e-3);
        let origin = (px + aim.0 / hl * MUZZLE_FORWARD_M, py + AIM_HEIGHT_M, pz + aim.2 / hl * MUZZLE_FORWARD_M);
        if st.rounds <= 0 {
            st.reload_t += dt;
            if st.reload_t > 3.0 {
                st.rounds = frame.gun_total_rounds;
                st.reload_t = 0.0;
                note(&mut st, "mech: rifle reloaded");
            }
        }
        // AC6 layout: R1 = right arm (rifle), L1 = left arm (blade), R2 / L2 = the back weapons (right / left shoulder)
        let right = (aim.2 / hl, -aim.0 / hl);
        let shoulder = |side: f32| {
            (
                px + aim.0 / hl * 0.8 + right.0 * side * 1.0,
                py + AIM_HEIGHT_M + 0.3,
                pz + aim.2 / hl * 0.8 + right.1 * side * 1.0,
            )
        };
        st.back_cd_r = (st.back_cd_r - dt).max(0.0);
        st.back_cd_l = (st.back_cd_l - dt).max(0.0);
        if st.back_rounds < 0 {
            st.back_rounds = frame.back_total_rounds;
        }
        // shots this frame: (bullet id, from a back weapon, origin)
        let mut shots: Vec<(i32, bool, (f32, f32, f32))> = Vec::new();
        // self test: a flag file fires three rifle shots and then the back weapon, no input needed
        {
            let flag = crate::log_dir().join("selftest_fire");
            if st.settle > 5.0 && flag.exists() {
                let mode = std::fs::read_to_string(&flag).unwrap_or_default();
                let _ = std::fs::remove_file(&flag);
                if mode.trim() == "dbg" {
                    st.dbg_i = 0;
                    st.dbg_t = 0.0;
                    log("selftest: effect parade armed");
                } else {
                    st.test_fire = 5;
                    st.test_fire_t = 0.0;
                    log("selftest: firing sequence armed");
                }
            }
            if st.dbg_i >= 0 {
                st.dbg_t += dt;
                if st.dbg_t > 0.8 {
                    st.dbg_t = 0.0;
                    shots.push((99919000 + st.dbg_i, false, origin));
                    log(&format!("selftest: parade shot {}", st.dbg_i));
                    st.dbg_i += 1;
                    if st.dbg_i >= DEBUG_SFX_COUNT {
                        st.dbg_i = -1;
                    }
                }
            }
            if st.test_fire > 0 {
                st.test_fire_t += dt;
                let wait = if st.test_fire == 5 { 0.2 } else { 1.0 };
                if st.test_fire_t > wait {
                    st.test_fire_t = 0.0;
                    if st.test_fire <= 2 && frame.back_bullet_id != 0 {
                        shots.push((frame.back_bullet_id, true, shoulder(if st.test_fire == 2 { 1.0 } else { -1.0 })));
                    } else {
                        shots.push((frame.gun_bullet_id, false, origin));
                    }
                    st.test_fire -= 1;
                }
            }
        }
        if inp.r1 && st.gun_cd <= 0.0 && st.rounds >= 1 {
            shots.push((frame.gun_bullet_id, false, origin));
            st.gun_cd = frame.gun_cooldown_s;
        }
        if frame.back_bullet_id != 0 {
            if inp.r2 && st.back_cd_r <= 0.0 && st.back_rounds >= 1 {
                shots.push((frame.back_bullet_id, true, shoulder(1.0)));
                st.back_cd_r = frame.back_cooldown_s;
            }
            if inp.l2 && st.back_cd_l <= 0.0 && st.back_rounds >= 1 {
                shots.push((frame.back_bullet_id, true, shoulder(-1.0)));
                st.back_cd_l = frame.back_cooldown_s;
            }
        }
        for (bid, is_back, from) in shots {
            match spawn_shot(player, bid, from, aim) {
                Ok(()) => {
                    if is_back {
                        st.back_rounds -= 1;
                    } else {
                        st.rounds -= 1;
                    }
                    st.bullet_watch = 90;
                    if st.shots_logged < 24 {
                        st.shots_logged += 1;
                        log(&format!(
                            "mech: shot bullet {bid} ({}) from {:.1},{:.1},{:.1} dir {:.2},{:.2},{:.2}, rounds left {} / back {}",
                            if is_back { "back" } else { "arm" }, from.0, from.1, from.2, aim.0, aim.1, aim.2, st.rounds, st.back_rounds
                        ));
                    }
                }
                Err(e) => {
                    if st.shots_logged < 24 {
                        st.shots_logged += 1;
                        log(&format!("mech: shot FAILED bullet {bid}, error {e}"));
                    }
                }
            }
        }
        // blade: a lunge forward with the swing (AC6 blade homing speed); the swing itself and its damage are the game's
        let l1_press = inp.l1 && !st.prev_l1;
        st.prev_l1 = inp.l1;
        if l1_press && st.lunge_left <= 0.0 {
            // the way the mech is actually moving (measured, not the learned stick map, whose guess sent the lunge off to the
            // side); standing still: straight along the camera's view (camera -> mech)
            let moving = st.last_pos.and_then(|(lx, _, lz)| {
                let (mx, mz) = (px - lx, pz - lz);
                let sp = (mx * mx + mz * mz).sqrt() / dt.max(1e-3);
                if sp > 2.0 && sp < 80.0 { norm2(mx, mz) } else { None }
            });
            let dir = if st.flying && (st.vel.0 * st.vel.0 + st.vel.1 * st.vel.1) > 4.0 {
                norm2(st.vel.0, st.vel.1)
            } else {
                moving
            }
            .or(Some((aim.0 / hl, aim.2 / hl)));
            if let Some((x, z)) = dir {
                st.lunge_dir = (x, z);
                st.lunge_left = LUNGE_S;
                st.lunge_acc = (0.0, 0.0);
                // the slash itself: a short fat energy bolt just ahead of the mech at chest height
                let slash_origin = (px + x * 1.5, py + AIM_HEIGHT_M * 0.75, pz + z * 1.5);
                if let Err(e) = spawn_shot(player, BLADE_BULLET_ID, slash_origin, (x, 0.0, z)) {
                    log(&format!("mech: slash bolt FAILED {e}"));
                }
                if st.lunge_logged < 6 {
                    st.lunge_logged += 1;
                    log(&format!("mech: blade lunge dir ({x:.2},{z:.2}) aim ({:.2},{:.2}) stick {:.2},{:.2}", aim.0 / hl, aim.2 / hl, inp.fwd, inp.right));
                }
            }
        }
        let lunge_was = st.lunge_left > 0.0;
        if st.lunge_left > 0.0 {
            st.lunge_left -= dt;
            let (ux, uz) = st.lunge_dir;
            let step = wall_step(player, px, py, pz, ux, uz, LUNGE_MPS * dt);
            let (nx, nz) = (px + ux * step, pz + uz * step);
            // on foot the lunge follows the ground (a rising slope used to push the capsule into the terrain); a step the
            // ground does not allow ends the lunge
            let mut ny = py;
            let mut ok = step > 0.0;
            if ok && !st.flying {
                match ground_top(player, nx, py, nz) {
                    Some(g) if g - py < 1.0 && py - g < 3.0 => ny = g,
                    _ => ok = false,
                }
            }
            if ok {
                let phys = &mut player.chr_ins.modules.physics;
                phys.position.0 = nx;
                phys.position.1 = ny;
                phys.position.2 = nz;
                st.lunge_acc.0 += nx - px;
                st.lunge_acc.1 += nz - pz;
                phys.chr_proxy_pos_update_requested = true;
            } else {
                st.lunge_left = 0.0;
            }
        }
        if lunge_was && st.lunge_left <= 0.0 && st.lunge_logged < 12 {
            st.lunge_logged += 1;
            log(&format!("mech: lunge done, moved {:.1} m (y {:.2})", (st.lunge_acc.0 * st.lunge_acc.0 + st.lunge_acc.1 * st.lunge_acc.1).sqrt(), py));
        }
        // keep the game's own attack out of the right hand (the gun has no swing)
        {
            let ar = &mut player.chr_ins.modules.action_request;
            ar.disabled_action_inputs.set_r1(true);
            ar.disabled_action_inputs.set_r2(true);
            ar.disabled_action_inputs.set_l2(true);
            // L1 stays enabled: the game plays its L1 combo clips, which are now the AC6 blade attack (no root motion); the mod
            // adds the lunge and the slash bolt
        }
        // input diagnostics: what the pad says while something is pressed
        if st.diag_in < 25 && (inp.mag() > 0.1 || inp.r1 || inp.r2 || inp.l1 || inp.l2 || inp.jump || inp.dodge) && st.heartbeat % 20 == 0 {
            st.diag_in += 1;
            log(&format!("mech: input fwd {:.2} right {:.2} jump {} dodge {} r1 {} r2 {} l1 {} l2 {} | grounded {}", inp.fwd, inp.right, inp.jump, inp.dodge, inp.r1, inp.r2, inp.l1, inp.l2, grounded));
        }
    }

    // ---- start a Quick Boost (on the ground, or in the air while flying) ----
    if pressed && (grounded || st.flying) && st.boost_left <= 0.0 && st.cooldown <= 0.0 && !st.overheated {
        let cost = (frame.qb_en_pct / 100.0 * max_fp).ceil();
        if fp as f32 >= cost {
            fp_set(player, fp - cost as i32);
            st.boost_cost = cost;
            st.since_spend = 0.0;
            st.boost_left = frame.qb_duration_s;
            st.boost_total = frame.qb_duration_s;
            st.boost_frames = 0;
            st.boost_start = (px, py, pz);
            st.boost_travelled = 0.0;
            st.boost_dir = None;
            if st.flying {
                // no roll in the air: boost along the stick, or straight ahead of the camera
                st.boost_dir = stick_world(&st, &inp, &cam, pos2, true).map(|(x, z, _)| (x, z));
            }
        } else {
            note(&mut st, "mech: boost refused, not enough EN");
        }
    }

    // ---- run the Quick Boost ----
    if st.boost_left > 0.0 && !st.flying && !grounded && st.boost_frames > DIRECTION_FRAMES {
        // went off a ledge: stop pushing, the game's own fall takes over
        st.boost_left = 0.0;
        st.cooldown = frame.qb_cooldown_s;
    }
    if st.boost_left > 0.0 {
        st.boost_frames += 1;
        // on the ground: read the direction the roll is moving after a few frames
        if st.boost_dir.is_none() && st.boost_frames >= DIRECTION_FRAMES {
            let dx = px - st.boost_start.0;
            let dz = pz - st.boost_start.2;
            let len = (dx * dx + dz * dz).sqrt();
            if len > 0.02 {
                st.boost_dir = Some((dx / len, dz / len));
            } else {
                // standing dodge (backstep): nothing to boost along, give the EN back
                let (fp2, _) = fp_now(player);
                fp_set(player, fp2 + st.boost_cost as i32);
                st.boost_left = 0.0;
            }
        }
        if let (Some((dx, dz)), true) = (st.boost_dir, st.boost_left > 0.0) {
            // ease-out speed: peak 2D/T falling to zero, so the whole boost adds D metres
            let t_frac = (st.boost_left / st.boost_total).clamp(0.0, 1.0);
            let boost_speed = 2.0 * frame.qb_distance_m / st.boost_total * t_frac;
            let step = wall_step(player, px, py, pz, dx, dz, boost_speed * dt);
            let phys = &mut player.chr_ins.modules.physics;
            phys.position.0 += dx * step;
            phys.position.2 += dz * step;
            phys.chr_proxy_pos_update_requested = true;
            st.boost_travelled += step;
        }
        st.boost_left -= dt;
        if st.boost_left <= 0.0 {
            st.boost_left = 0.0;
            st.cooldown = frame.qb_cooldown_s;
            let travelled = st.boost_travelled;
            note(&mut st, &format!("mech: boost done, added {travelled:.2} m"));
        }
    }
    st.cooldown = (st.cooldown - dt).max(0.0);
    {
        let p = &player.chr_ins.modules.physics.position;
        st.last_pos = Some((p.0, p.1, p.2));
    }

    // ---- energy refills after the generator's delay (not while thrusting, boosting or overheated) ----
    st.since_spend += dt;
    let thrusting_now = (st.flying && inp.jump && can_thrust) || want_hold;
    if st.boost_left <= 0.0 && !thrusting_now && !st.overheated && st.since_spend >= frame.en_regen_delay_s {
        let gain = frame.en_regen_pct_per_s / 100.0 * max_fp * dt + st.fp_carry;
        let whole = gain.floor();
        st.fp_carry = gain - whole;
        if whole >= 1.0 {
            let (cur, _) = fp_now(player);
            fp_set(player, cur + whole as i32);
        }
    }
}

pub fn start() {
    log("mech: waiting for the game's task system");
    let tasks = match CSTaskImp::wait_for_instance(Duration::from_secs(180)) {
        Ok(t) => t,
        Err(e) => {
            log(&format!("mech: task system never appeared: {e:?}"));
            return;
        }
    };
    let handle = tasks.run_recurring(RecurringTask::new(tick), CSTaskGroupIndex::ChrIns_PrePhysics);
    // the task lives as long as the process
    std::mem::forget(handle);
    log("mech: per-frame task registered");
}
