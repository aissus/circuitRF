#!/usr/bin/env python3
"""An independent calculation of the example's front end with its canceller, for the test suite to compare with.

    python3 canceller_reference.py            # writes canceller_reference.json beside this file

Standard library only. The chain is examples/RF Chain Budget/Front End.cchain, its numbers written out below. The
canceller's reference path is a Tanh-law amplifier, its intercept stated at its output; its terms for two equal tones
come from the law itself, its two-tone Fourier coefficients integrated numerically over both tones' phases, and beside
them the fifth-order series the budget takes:

    gain of each tone    (1/(2 pi^2)) int int tanh(u cos a + u cos b) cos a da db / u,   u = 2 sqrt(y), y = P/OIP3
                         series 1 - 3y + (40/3) y^2
    IM3 (2a - b)         the same with cos(2a - b), over the cubic's u^3/4;              series 1 - (20/3) y
    residual             1 + A^2 - 2 A cos(phi - 2 pi nu tau) sinc(pi B tau),  A = 10^(AmpErr/20) g
    later stages         Friis on the gains, the intercepts summed in phase from the canceller's output on; each
                         product 2 P1 + P2 - 2 OIP3, the one at 2 f_r - f_s with the canceller's IM3 times the residual
                         and the signal beside it, twice the amplitude, in phase; the signal's compression by two
                         residual tones and two IM3 products, 1 - 4 (P_r + P_c) / IIP3
"""
import json
import math
import os

K, T0 = 1.380649e-23, 290.0


def lin(db): return 10 ** (db / 10)
def db(x): return 10 * math.log10(x)


# ---- the chain ------------------------------------------------------------------------------------------------------
F0, B, PIN, TS = 2.45e9, 4.5e6, -60.0, 290.0
C_DB, IL, PINT, BINT, PREF, NFREF = 10.0, 0.2, -40.0, 10e6, -30.0, 6.0
AMP, PHASE, DELAY, IP3 = 0.02, 0.2, 0.05e-9, -5.0
# The stages after it: (name, gain dB, NF dB, IIP3 dBm); the Amps' intercepts are stated at their outputs.
LATER = [("LNA", 18.0, 1.2, 20.0 - 18.0), ("BPF", -1.0, 1.0, math.inf), ("AMP2", 14.0, 3.0, 25.0 - 14.0)]
HB_TONES = (-0.25, 0.35)          # the harmonic-balance part's interference tones, as a share of BINT


# ---- the Tanh law's two-tone coefficients, by quadrature ------------------------------------------------------------
def two_tone(y, n=256):
    """Each tone's gain and the IM3 product's amplitude over the cubic's (u^3/4), for two tones at a share y of the
    intercept: the periodic integrand's midpoint rule, which is exact to round-off at this n."""
    u = 2 * math.sqrt(y)
    h = 2 * math.pi / n
    c1 = c3 = 0.0
    for i in range(n):
        a = (i + 0.5) * h
        for j in range(n):
            b = (j + 0.5) * h
            t = math.tanh(u * (math.cos(a) + math.cos(b)))
            c1 += t * math.cos(a)
            c3 += t * math.cos(2 * a - b)
    c1 *= h * h / (2 * math.pi ** 2)
    c3 *= h * h / (2 * math.pi ** 2)
    return c1 / u, -c3 / (u ** 3 / 4)


def series_gain(y): return 1 - 3 * y + 40.0 / 3 * y * y
def series_im3(y): return 1 - 20.0 / 3 * y


def bisect(f, a, b, n=200):
    fa = f(a)
    for _ in range(n):
        m = 0.5 * (a + b)
        fm = f(m)
        if (fm > 0) == (fa > 0): a, fa = m, fm
        else: b = m
    return 0.5 * (a + b)


c = lin(-C_DB)
lt = IL - db(1 - c)                                 # the through arm's loss
gref = PINT + db((1 - c) / c) - PREF               # the reference path's gain the subtraction needs
f_canc = lin(lt) + (lin(NFREF) * lin(gref) - 1) * c / (1 - c)
pro0 = PINT + db((1 - c) / c)                       # its output per tone at no error
pro = pro0 + AMP
y = lin(pro - IP3)
g_series, g_tanh = series_gain(y), two_tone(y)[0]
k3_series, k3_tanh = series_im3(y), two_tone(y)[1]


def residual(amp_db, phase_deg, delay_s, bw_hz, nu_hz, gain):
    a = 10 ** (amp_db / 20) * gain
    x = math.pi * bw_hz * delay_s
    sinc = 1.0 if abs(x) < 1e-12 else math.sin(x) / x
    return 1 + a * a - 2 * a * math.cos(math.radians(phase_deg) - 2 * math.pi * nu_hz * delay_s) * sinc


r_flat = residual(AMP, PHASE, DELAY, BINT, 0, g_series)
depth = -db(r_flat)
res = PINT - lt - depth
im3 = 3 * pro - 2 * IP3 + 20 * math.log10(k3_series) + db(c) - IL
op1db = IP3 - 8.9625 - 1


def null_amp(gain_of):
    return 20 * math.log10(bisect(lambda a: a * gain_of(a * a * lin(pro0 - IP3)) - 1, 1.0, 2.0))


amp_null = null_amp(series_gain)
amp_null_tanh = null_amp(lambda yy: two_tone(yy, 128)[0])
im3_null = 3 * (pro0 + amp_null) - 2 * IP3 + 20 * math.log10(series_im3(lin(pro0 + amp_null - IP3))) + db(c) - IL

# Each harmonic-balance tone's residual at the stage's settings.
tone = {}
for name, share in zip(("A", "B"), HB_TONES):
    tone[name] = PINT - lt + db(residual(AMP, PHASE, DELAY, 0, share * BINT, g_series))

# ---- the cascade, and the residual through the stages after the canceller ---------------------------------------------
te, gn, sig = T0 * (f_canc - 1), lin(-lt), PIN - lt
noise_canc = db(K * (TS + te) * B * gn) + 30
nodes = [dict(node="CANC", sig=sig, res=res, canc_im3=im3)]
g_after, inv3, desense, pr = 1.0, 0.0, 0.0, res
for name, g, nf, iip3 in LATER:
    pc = im3 + db(g_after)                          # the canceller's own IM3 at this stage's input
    if math.isfinite(iip3):
        inv3 += g_after / lin(iip3)
        desense += 20 * math.log10(1 - 4 * (lin(pr - iip3) + lin(pc - iip3)))
    g_after *= lin(g)
    pr += g
    sig += g
    pc += g
    oip3 = -db(inv3) + db(g_after) if inv3 > 0 else math.inf
    cross = 20 * math.log10(1 + 2 * 10 ** ((pc - pr) / 20))
    nodes.append(dict(node=name, sig=sig, res=pr, canc_im3=pc,
                      res_im3=3 * pr - 2 * oip3, im_ssr=2 * sig + pr - 2 * oip3, im_rrs=2 * pr + sig - 2 * oip3 + cross,
                      desense=desense))

# ---- Tune canceller: its target, and how near the optimum the goal holds the settings -------------------------------------
noise_bint = noise_canc + db(BINT / B)
floor = max(noise_bint, im3_null)
target = floor - 20
r_goal = lin(target - (PINT - lt))                  # the residual fraction the goal allows, each tone's mean
nus = [s * BINT for s in HB_TONES]
tol = dict(amp_dB=20 * math.log10(1 + math.sqrt(r_goal)), phase_deg=math.degrees(math.sqrt(r_goal)),
           delay_s=math.sqrt(r_goal / (sum((2 * math.pi * nu) ** 2 for nu in nus) / len(nus))))

out = dict(
    through_loss_dB=lt, ref_gain_dB=gref, nf_canc_dB=db(f_canc), ref_out_dBm=pro, share=y,
    gain_series=g_series, gain_tanh=g_tanh, im3_factor_series=k3_series, im3_factor_tanh=k3_tanh,
    compression_dB=20 * math.log10(g_series), op1db_dBm=op1db, depth_dB=depth, residual_dBm=res, im3_dBm=im3,
    amp_null_dB=amp_null, amp_null_tanh_dB=amp_null_tanh, im3_null_dBm=im3_null,
    res_tone_A_dBm=tone["A"], res_tone_B_dBm=tone["B"],
    noise_canc_dBm=noise_canc, floor_dBm=floor, target_dBm=target, tolerance=tol,
    nodes=nodes,
)
with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "canceller_reference.json"), "w") as fh:
    json.dump(out, fh, indent=1, default=lambda v: None)
for k, v in out.items():
    if k != "nodes": print(f"{k:22s} {v}")
for n in nodes: print(n)
