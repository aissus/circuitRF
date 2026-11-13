#!/usr/bin/env python3
"""An independent calculation of what the RF chain budget says an LO's phase noise and the blockers do to a receiver: the
phase noise in the channel and its EVM floor, each blocker's reciprocal mixing and its desensitisation by compression,
the EVM at every node and the SFDR, for the test suite to compare with.

    python3 blocking_reference.py        # writes blocking_reference.json beside this file

Standard library only, and by numbers rather than the budget's closed forms: every integral below is a quadrature, and a
law's small-signal gain beside a blocker is read off the law itself by projecting a weak tone's output onto it.

Case "superhet": the example's superhet with its LO's mask and its three blockers.

    the mask: L(f) a straight line in dB against log f between its points, the first segment continued below the first,
        the last level beyond the last
    phase noise in the channel:   s2 = 2 * int_f1^B L(f) (1 - f/B) df          the share of x e^(j phi) that falls in a
        channel of width B (x's flat spectrum convolved with phi's, L the phase's two-sided density)
    reciprocal mixing:            N = P_b * int L(|u|) du over the channel, u from df - B/2 to df + B/2, at the mixer's
        input (Razavi, RF Microelectronics, 2nd ed., ch. 8: P_b + L(df) + 10 log10 B where L is flat across it)
    desensitisation by RM:        10 log10(1 + N / N_floor)
    compression, a tanh limiter:  a weak tone's gain beside a blocker of amplitude A is the time average of the slope,
        <u'(A cos t)>, u = Vsat tanh(x / Vsat), IIP3 = 2 Vsat (Razavi ch. 2: the cubic's a1 - (3/2) a3 A^2)
    EVM:                          sqrt(N/S + s2 + N_rm/S + (4/3)(P/IIP3)^2), the last a cubic's in-channel distortion of
        a Gaussian signal (Isserlis' theorem, as the ACLR relation's)
    SFDR:                         (2/3)(IIP3 - MDS)

Case "laws": one amplifier of each law (tanh, Rapp, Saleh) and a blocker at its input, at five levels: the weak tone's gain
beside it, F(|B + a e^(jt)|^2)(B + a e^(jt)) projected onto e^(jt) over a beat cycle for the envelope laws (Rapp 1991,
Saleh 1981), the instantaneous limiter's over both tones' cycles for tanh.
"""
import cmath
import json
import math
import os

K, T0 = 1.380649e-23, 290.0
HERE = os.path.dirname(os.path.abspath(__file__))


def lin(db):
    return 10 ** (db / 10)


def db(x):
    return 10 * math.log10(x)


def w(dbm):
    return 1e-3 * lin(dbm)


def st(name, part, **p):
    return {"Name": name, "Part": part, "Parameters": {k: str(v) for k, v in p.items()}}


# ── the mask ─────────────────────────────────────────────────────────────────

def mask_of(text):
    pts = []
    for item in text.split(","):
        f, l = item.split(":")
        f = f.strip()
        f = f[:-2] if f.endswith("Hz") else f
        scale = {"k": 1e3, "M": 1e6, "G": 1e9}.get(f[-1], 1)
        pts.append((float(f.rstrip("kMG")) * scale, float(l)))
    return pts


def L(pts, f):
    if f > pts[-1][0]:
        return pts[-1][1]
    i = 0
    if f >= pts[0][0]:
        while i < len(pts) - 2 and f > pts[i + 1][0]:
            i += 1
    t = (math.log10(f) - math.log10(pts[i][0])) / (math.log10(pts[i + 1][0]) - math.log10(pts[i][0]))
    return pts[i][1] + t * (pts[i + 1][1] - pts[i][1])


def quad(g, a, b, n=4000):
    """int_a^b g(f) df by Simpson's rule in log f on each decade-ish piece (a, b > 0)."""
    if b <= a:
        return 0.0
    la, lb = math.log(a), math.log(b)
    h = (lb - la) / n
    s = 0.0
    for k in range(n + 1):
        x = la + k * h
        f = math.exp(x)
        c = 1 if k in (0, n) else (4 if k % 2 else 2)
        s += c * g(f) * f
    return s * h / 3


def quad_pieces(pts, g, a, b):
    """Simpson's rule between the mask's points, where its slope breaks."""
    cuts = [a] + [f for f, _ in pts if a < f < b] + [b]
    return sum(quad(g, cuts[i], cuts[i + 1]) for i in range(len(cuts) - 1))


def in_channel(pts, bw):
    return 2 * quad_pieces(pts, lambda f: lin(L(pts, f)) * (1 - f / bw), pts[0][0], bw)


def reciprocal(pts, df, bw):
    f1, lo, hi = pts[0][0], df - bw / 2, df + bw / 2
    s = quad_pieces(pts, lambda f: lin(L(pts, f)), max(lo, f1), hi)
    if lo < -f1:
        s += quad_pieces(pts, lambda f: lin(L(pts, f)), f1, -lo)
    return s


# ── a weak tone's gain beside a blocker ─────────────────────────────────────

def tanh_ratio(pb_dbm, iip3_dbm, n1=4000, n2=16):
    """|gain|^2 over small-signal: u = tanh(x) with x = A cos t1 + a cos t2 (Vsat = 1, A = 2 sqrt(P/IIP3)), the output at
    cos t2 over a, on a grid of both phases."""
    amp, a = 2 * math.sqrt(lin(pb_dbm - iip3_dbm)), 1e-4
    s = 0.0
    for i in range(n1):
        c1 = amp * math.cos(2 * math.pi * (i + 0.5) / n1)
        for j in range(n2):
            c2 = math.cos(2 * math.pi * (j + 0.5) / n2)
            s += math.tanh(c1 + a * c2) * c2
    g = 2 * s / (n1 * n2) / a
    return g * g


def envelope_ratio(F, pb_w, n=256):
    """|gain|^2 over small-signal of an envelope law F(s), s the available power: (B + a e^(jt)) through it, projected
    onto e^(jt) over a cycle."""
    b, a = math.sqrt(pb_w), 1e-6 * math.sqrt(pb_w) if pb_w > 0 else 1e-9
    acc = 0j
    for k in range(n):
        e = cmath.exp(1j * 2 * math.pi * (k + 0.5) / n)
        z = b + a * e
        acc += F(abs(z) ** 2) * z * e.conjugate()
    g = acc / n / a
    return abs(g) ** 2


def rapp(psat_dbm, g_db, p):
    p0 = w(psat_dbm - g_db)
    return lambda s: (1 + (s / p0) ** p) ** (-1 / (2 * p))


def saleh(psat_dbm, g_db, phisat, phimax):
    ssat = 4 * w(psat_dbm - g_db)
    bb = phisat / (phimax - phisat)
    aa = math.radians(phimax) * bb
    return lambda s: cmath.exp(1j * aa * (s / ssat) / (1 + bb * s / ssat)) / (1 + s / ssat)


# ── the superhet ─────────────────────────────────────────────────────────────

MASK = "1kHz:-95, 100kHz:-95, 1MHz:-115, 10MHz:-130, 30MHz:-140"
FLO = 2.1e9
SUPERHET = {
    "FormatVersion": 5, "FrequencyHz": 2.45e9, "BandwidthHz": 4.5e6, "InputPowerDbm": -70, "TemperatureK": 290,
    "RequiredSnrDb": 10,
    "Stages": [
        st("LNA", "Amp", Gain=16, NF=1, IP3=15, IP3Ref="Output"),
        st("RFBPF", "Filter", Response="Butterworth", Form="Bandpass", Order=3, Fc="1 GHz", F1="2.35 GHz", F2="2.55 GHz",
           Ripple=0.1, Astop=40, IL=1.5),
        st("MIX", "Mixer", ConvGain=-7, NF=7, NFType="ssb", IIP3=10, Plo=7, Flo="2.1 GHz", PhaseNoise=MASK),
        st("IFBPF", "Filter", Response="Butterworth", Form="Bandpass", Order=3, Fc="1 GHz", F1="320 MHz", F2="380 MHz",
           Ripple=0.1, Astop=40, IL=1),
        st("IFA", "Amp", Gain=20, NF=4, IP3=25, IP3Ref="Output"),
        st("ATT", "Atten", Loss=3),
    ],
    "Blockers": [{"OffsetHz": 150e6, "LevelDbm": -40}, {"OffsetHz": 20e6, "LevelDbm": -50}, {"OffsetHz": -12e6, "LevelDbm": -45}],
    "Demo": True, "DynamicRange": True, "Records": 4,
}


def butterworth21(f, f1, f2, n, il):
    wn = (f * f - f1 * f2) / ((f2 - f1) * f)
    return lin(-il) / (1 + wn ** (2 * n))


def superhet_case():
    d = SUPERHET
    f, bw, pin, ts = d["FrequencyHz"], d["BandwidthHz"], d["InputPowerDbm"], d["TemperatureK"]
    pts = mask_of(MASK)
    # (gain at the carrier, NF, IIP3 at the stage's input or None) — each at its own carrier, the filters their IL.
    stages = [(16, 1, 15 - 16), (-1.5, 1.5, None), (-7, 7, 10), (-1, 1, None), (20, 4, 25 - 20), (-3, 3, None)]
    # Each blocker at every node: a filter its Butterworth response there, the mixer moving it to |f - Flo|.
    def through(i, level, fb):
        if i == 1:
            return level + db(butterworth21(fb, 2.35e9, 2.55e9, 3, 1.5)), fb
        if i == 3:
            return level + db(butterworth21(fb, 320e6, 380e6, 3, 1)), fb
        if i == 2:
            return level + stages[i][0], abs(fb - FLO)
        return level + stages[i][0], fb
    blk = [[(b["LevelDbm"], f + b["OffsetHz"])] for b in d["Blockers"]]
    for k in range(len(blk)):
        for i in range(len(stages)):
            blk[k].append(through(i, *blk[k][-1]))
    # The cascade at every node: gain, Te (Friis), IIP3.
    g, te, inv3, sig_hz = [0.0], [0.0], [0.0], [f]
    gl = 1.0
    for i, (gs, nf, ip3) in enumerate(stages):
        te.append(te[-1] + T0 * (lin(nf) - 1) / gl)
        if ip3 is not None:
            inv3.append(inv3[-1] + gl / w(ip3))
        else:
            inv3.append(inv3[-1])
        gl *= lin(gs)
        g.append(db(gl))
        sig_hz.append(abs(sig_hz[-1] - FLO) if i == 2 else sig_hz[-1])
    nodes = len(stages) + 1
    noise = [db(K * (ts + te[k]) * bw * lin(g[k])) + 30 for k in range(nodes)]
    signal = [pin + g[k] for k in range(nodes)]
    # The mask enters at the mixer's output (node 3); each blocker's reciprocal mixing at its input (node 2), at its
    # offset there, carried to each node by the gain after it.
    s2 = in_channel(pts, bw)
    rm_in = [w(blk[k][2][0]) * reciprocal(pts, abs(blk[k][2][1] - sig_hz[2]), bw) for k in range(len(blk))]
    pn_dbc, evm, rm_dbm, des_rm = [], [], [], []
    for k in range(nodes):
        after = k >= 3
        rm = [x * lin(g[k] - g[2]) if after else 0.0 for x in rm_in]
        s, nn = w(signal[k]), w(noise[k])
        dist = 4 / 3 * (w(pin) * inv3[k]) ** 2
        pn = s2 if after else 0.0
        pn_dbc.append(db(pn) if pn > 0 else None)
        rm_dbm.append([db(x) + 30 if x > 0 else None for x in rm])
        des_rm.append([db(1 + x / nn) for x in rm])
        evm.append(100 * math.sqrt(nn / s + pn + sum(rm) / s + dist))
    # Compression: each blocker at each stage's input through the stage's tanh, the gain lost summed to each node.
    des_c = []
    for k in range(len(blk)):
        acc, row = 0.0, [0.0]
        for i, (_, _, ip3) in enumerate(stages):
            if ip3 is not None:
                acc -= db(tanh_ratio(blk[k][i][0], ip3))
            row.append(acc)
        des_c.append(row)
    iip3 = -db(inv3[-1]) + 30
    mds = db(K * (ts + te[-1]) * bw) + 30
    out = {
        "design": d,
        "PN_dBc": [p for p in pn_dbc],
        "EVM_pct": evm,
        "RM_dBm": rm_dbm,
        "DesenseRM_dB": des_rm,
        "DesenseComp_dB": [[des_c[k][n] for k in range(len(blk))] for n in range(nodes)],
        "SFDR_dB": 2 / 3 * (iip3 - mds),
        "MDS_dBm": mds,
        "IIP3_dBm": iip3,
    }
    rest = w(noise[-1] - signal[-1] + 30) + s2 + sum(rm_in[k] * lin(g[-1] - g[2]) for k in range(len(blk))) / w(signal[-1])
    out["SNReff_dB"] = -db(rest)
    out["EVMpn_pct"] = 100 * math.sqrt(s2)
    return out


# ── the laws ────────────────────────────────────────────────────────────────

LAWS = [
    ("Tanh", st("PA", "Amp", Gain=20, NF=3, IP3=30, IP3Ref="Output")),
    ("Rapp", st("PA", "Amp", Gain=20, NF=3, Law="Rapp", Psat=20, P=2)),
    ("Saleh", st("PA", "Amp", Gain=20, NF=3, Law="Saleh", Psat=20, PhiSat=10, PhiMax=30)),
]
LEVELS = [-30, -20, -15, -10, -5]


def laws_case():
    out = []
    for name, stage in LAWS:
        if name == "Tanh":
            ratio = lambda p: tanh_ratio(p, 30 - 20)
        elif name == "Rapp":
            F = rapp(20, 20, 2)
            ratio = lambda p, F=F: envelope_ratio(F, w(p))
        else:
            F = saleh(20, 20, 10, 30)
            ratio = lambda p, F=F: envelope_ratio(F, w(p))
        out.append({"law": name, "stage": stage, "levels": LEVELS, "DesenseComp_dB": [-db(ratio(p)) for p in LEVELS]})
    return out


def main():
    res = {"superhet": superhet_case(), "laws": laws_case()}
    with open(os.path.join(HERE, "blocking_reference.json"), "w") as fh:
        json.dump(res, fh, indent=1)
    s = res["superhet"]
    print(f"superhet: PN {s['PN_dBc'][-1]:.4f} dBc, EVM floor {s['EVMpn_pct']:.4f} %, SNR {s['SNReff_dB']:.4f} dB, "
          f"SFDR {s['SFDR_dB']:.4f} dB")
    for k in range(3):
        print(f"  blocker {k + 1}: RM {s['DesenseRM_dB'][-1][k]:.4f} dB, compression {s['DesenseComp_dB'][-1][k]:.6f} dB")
    print("  EVM per node:", " ".join(f"{x:.4f}" for x in s["EVM_pct"]))
    for c in res["laws"]:
        print(f"{c['law']}: " + ", ".join(f"{p} dBm {x:.5f} dB" for p, x in zip(c["levels"], c["DesenseComp_dB"])))


if __name__ == "__main__":
    main()
