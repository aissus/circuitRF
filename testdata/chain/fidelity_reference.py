#!/usr/bin/env python3
"""An independent calculation of the RF chain budget's in-band response, image noise and cascade with mismatch, for the
test suite to compare with.

    python3 fidelity_reference.py        # writes fidelity_reference.json and fid_a1.s2p, fid_a2.s2p beside this file

Standard library only. Each design below is written out as a .cchain (the "design" of each case, which the test reads
back through circuitRF); this script works it out with its own arithmetic, from the textbook forms.

Case "response": a superhet whose RF filter passes the mixer's image, budgeted across +-60 MHz with its LO fixed.

    a Butterworth bandpass of order n, between 50 ohm:  |S21|^2 = L / (1 + W^(2n)),  |S22|^2 = W^(2n) / (1 + W^(2n)),
        W = (f^2 - f1 f2) / ((f2 - f1) f), L = 10^(-IL/10)                      (Pozar, Microwave Engineering, 4th ed., 8.3-8.4;
                                                                                  the flat IL on S21 alone)
    its noise factor from 50 ohm: F = (1 - |S22|^2) / |S21|^2                   (Bosma: a passive network at T0)
    Friis: Te = sum Te_i / G_(i-1); 1/IIP3 = sum G_(i-1)/IIP3_i; IP1dB_i = IIP3_i - 8.9625 dB (tanh)
    the image f_im = |f - 2 Flo|: what reaches the mixer there, behind the isolator a bench places in front of it,
        T_im = |S21(f_im)|^2 T_lna + T0 (1 - |S21|^2 - |S22|^2) + T0 |S22(f_im)|^2,  T_lna = G_lna (T0 + Te_lna),
    and the SSB figure already holds T0 of it:  dTe = (T_im - T0) / (G_lna |S21(f)|^2)  (Razavi, RF Microelectronics,
        2nd ed., ch. 6: a mixer converts both sidebands, and an SSB figure takes the image band at T0)

Case "mismatch": two amplifiers from files, their ports reflecting, at 2.45 GHz, by Friis' formula with AVAILABLE gain and
each stage's noise factor at the source it really sees (Friis 1944; Pozar 10.2, 12.1, 12.3):

    F1 = F(0);  Gamma_out1 = S22a (a 50 ohm source);  G_A1 = |S21a|^2 / (1 - |S22a|^2)
    F2 = F(Gamma_out1),  F(Gs) = Fmin + 4 rn |Gs - Gopt|^2 / ((1 - |Gs|^2) |1 + Gopt|^2)
    F = F1 + (F2 - 1) / G_A1
    G_T between 50 ohm = |S21a S21b / (1 - S22a S11b)|^2

which circuitRF does another way, by the stages' chain correlation matrices (Hillbrand & Russer 1976).
"""
import cmath
import json
import math
import os

K, T0 = 1.380649e-23, 290.0
TANH = 8.9625
HERE = os.path.dirname(os.path.abspath(__file__))


def st(name, part, **p):
    return {"Name": name, "Part": part, "Parameters": {k: str(v) for k, v in p.items()}}


def lin(db):
    return 10 ** (db / 10)


def db(x):
    return 10 * math.log10(x)


# ── the response, and the image ──────────────────────────────────────────────

FLO = 2.1e9
RESPONSE = {
    "FormatVersion": 4, "FrequencyHz": 2.45e9, "BandwidthHz": 4.5e6, "InputPowerDbm": -70, "TemperatureK": 290,
    "RequiredSnrDb": 10,
    "Stages": [
        st("LNA", "Amp", Gain=16, NF=1, IP3=15, IP3Ref="Output"),
        st("RFBPF", "Filter", Response="Butterworth", Form="Bandpass", Order=3, F1="1.6 GHz", F2="3.6 GHz", IL=1.5),
        st("MIX", "Mixer", ConvGain=-7, NF=7, NFType="ssb", IIP3=10, Plo=7, Flo="2.1 GHz"),
        st("IFBPF", "Filter", Response="Butterworth", Form="Bandpass", Order=3, F1="320 MHz", F2="380 MHz", IL=1),
        st("IFA", "Amp", Gain=20, NF=4, IP3=25, IP3Ref="Output"),
        st("ATT", "Atten", Loss=3),
    ],
    "Response": {"From": -60e6, "To": 60e6, "Points": 7, "Offset": True},
}


def butterworth(f, f1, f2, n, il):
    """|S21|^2 and |S22|^2 of a Butterworth bandpass with a flat loss, between 50 ohm."""
    w = (f * f - f1 * f2) / ((f2 - f1) * f)
    p = w ** (2 * n)
    return lin(-il) / (1 + p), p / (1 + p)


def rf(f):
    return butterworth(f, 1.6e9, 3.6e9, 3, 1.5)


def iff(f):
    return butterworth(f, 320e6, 380e6, 3, 1)


def point(f, carrier):
    """The chain at input frequency f: its gains, noise and intercepts. At the carrier a filter is its IL (the single
    budget's passband); off it, its own response."""
    g_rf, r_rf = (lin(-1.5), 0.0) if carrier else rf(f)
    g_if, r_if = (lin(-1), 0.0) if carrier else iff(f - FLO)
    nf_mix = 7
    # (gain, noise factor from 50 ohm, input intercept dBm) per stage
    stages = [(lin(16), lin(1), 15 - 16), (g_rf, (1 - r_rf) / g_rf, math.inf), (lin(-7), lin(nf_mix), 10),
              (g_if, (1 - r_if) / g_if, math.inf), (lin(20), lin(4), 25 - 20), (lin(-3), lin(3), math.inf)]
    g, te, inv3, inv1 = 1.0, 0.0, 0.0, 0.0
    for gi, fi, ip in stages:
        te += T0 * (fi - 1) / g
        if math.isfinite(ip):
            inv3 += g / lin(ip)
            inv1 += g / lin(ip - TANH)
        g *= gi
    # The image, behind the isolator: the RF filter's response there, and the LNA's noise.
    fim = abs(f - 2 * FLO)
    g_im, r_im = rf(fim)
    t_lna = lin(16) * (T0 + T0 * (lin(1) - 1))
    t_im = g_im * t_lna + T0 * (1 - g_im - r_im) + T0 * r_im
    dte = (t_im - T0) / (lin(16) * g_rf)
    iip3, ip1 = -db(inv3), -db(inv1)
    gdb = db(g)
    return {"Gain_dB": gdb, "NF_dB": db(1 + (te + dte) / T0), "IIP3_dBm": iip3, "OIP3_dBm": iip3 + gdb,
            "IP1dB_dBm": ip1, "OP1dB_dBm": ip1 + gdb - 1, "Pout_dBm": -70 + gdb, "Fout_Hz": f - FLO,
            "_image": db(1 + (te + dte) / T0) - db(1 + te / T0)}


def response_case():
    offs = [-60e6 + i * 20e6 for i in range(7)]
    pts = [point(2.45e9 + o, carrier=False) for o in offs]
    at = point(2.45e9, carrier=True)
    exp = {k: [p[k] for p in pts] for k in pts[0] if not k.startswith("_")}
    return {"name": "response", "design": RESPONSE,
            "expected": {"Response": exp, "NFtot_dB": at["NF_dB"], "NFimage_dB": at["_image"], "Gain_dB": at["Gain_dB"]}}


# ── the cascade with mismatch ────────────────────────────────────────────────

A1 = {"S11": (0.35, -60), "S21": (6.0, 110), "S12": (0.05, 20), "S22": (0.4, -40), "noise": (0.9, 0.35, 45, 0.25)}
A2 = {"S11": (0.3, 150), "S21": (4.0, -70), "S12": (0.03, 10), "S22": (0.25, 80), "noise": (2.0, 0.2, -30, 0.4)}


def c(ma):
    return cmath.rect(ma[0], math.radians(ma[1]))


def write_s2p(name, a):
    with open(os.path.join(HERE, name), "w") as fh:
        fh.write("! fidelity_reference.py: a flat amplifier with reflecting ports and noise data\n# GHz S MA R 50\n")
        for f in (1, 2, 3, 4):
            fh.write(f"{f} {a['S11'][0]} {a['S11'][1]} {a['S21'][0]} {a['S21'][1]} {a['S12'][0]} {a['S12'][1]} {a['S22'][0]} {a['S22'][1]}\n")
        for f in (1, 4):
            nf, gm, gd, rn = a["noise"]
            fh.write(f"{f} {nf} {gm} {gd} {rn}\n")


def noise_factor(a, gs):
    nf, gm, gd, rn = a["noise"]
    gopt = cmath.rect(gm, math.radians(gd))
    return lin(nf) + 4 * rn * abs(gs - gopt) ** 2 / ((1 - abs(gs) ** 2) * abs(1 + gopt) ** 2)


def mismatch_case():
    s21a, s22a, s11b, s21b = c(A1["S21"]), c(A1["S22"]), c(A2["S11"]), c(A2["S21"])
    f1 = noise_factor(A1, 0)
    ga1 = abs(s21a) ** 2 / (1 - abs(s22a) ** 2)
    f2 = noise_factor(A2, s22a)
    f = f1 + (f2 - 1) / ga1
    gt = abs(s21a * s21b / (1 - s22a * s11b)) ** 2
    design = {"FormatVersion": 4, "FrequencyHz": 2.45e9, "BandwidthHz": 1e6, "InputPowerDbm": -60, "TemperatureK": 290,
              "RequiredSnrDb": 10, "Mismatch": True,
              "Stages": [st("A1", "SnP", File="fid_a1.s2p"), st("A2", "SnP", File="fid_a2.s2p")]}
    return {"name": "mismatch", "design": design,
            "expected": {"G_dB": [db(abs(s21a) ** 2), db(gt)], "NF_dB": [db(f1), db(f)], "Gain_dB": db(gt), "NFtot_dB": db(f)}}


if __name__ == "__main__":
    write_s2p("fid_a1.s2p", A1)
    write_s2p("fid_a2.s2p", A2)
    out = os.path.join(HERE, "fidelity_reference.json")
    with open(out, "w") as fh:
        json.dump({"cases": [response_case(), mismatch_case()]}, fh, indent=1)
        fh.write("\n")
    print(f"wrote {out}")
