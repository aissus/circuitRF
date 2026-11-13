#!/usr/bin/env python3
"""An independent calculation of the example's linear transmitter, for the test suite to compare with.

    python3 tx_reference.py              # writes tx_reference.json beside this file

Standard library only. The chain is examples/RF Chain Budget/Linear TX.cchain, its numbers written out below; each
readout comes from its textbook form, and each amplifier law's 1 dB point and saturation by root-finding the law's
own AM-AM rather than from its closed form:

    Friis on the gains; 1/IIP3 = sum of G_(i-1)/IIP3_i; IP1dB likewise; OP1dB = IP1dB + G - 1
    Rapp  gain (1 + (s/P0)^p)^(-1/(2p)), P0 = Psat/G; its intercept the cubic's at its 1 dB point, IP1dB + 9.64 dB
    Saleh gain e^(j a v/(1 + b v))/(1 + v), v = s/s_sat, s_sat = 4 Psat/G
    tanh  its describing function (2/(pi u)) int_0^pi tanh(u cos t) cos t dt, u = 2 sqrt(P/IIP3), by Simpson's rule
    ACLR  2 (Pout - OIP3) - 10 log10(3)
    the receive band: each stage's gain at its frequency; a filter passes |S21|^2 and adds T0 (1 - |S21|^2 - |S22|^2)
    the IQ modulator: wanted |1 + g e^(j phi)|^2/4, image |1 - g e^(j phi)|^2/4, LO Plo - IsoLO_IF + 10 log10(1 - sin phi)
"""
import cmath
import json
import math
import os

K, T0 = 1.380649e-23, 290.0


def lin(db): return 10 ** (db / 10)
def db(x): return 10 * math.log10(x)
def w(dbm): return 1e-3 * lin(dbm)


def bisect(f, a, b, n=200):
    fa = f(a)
    for _ in range(n):
        m = 0.5 * (a + b)
        fm = f(m)
        if (fm > 0) == (fa > 0): a, fa = m, fm
        else: b = m
    return 0.5 * (a + b)


# ---- the chain ------------------------------------------------------------------------------------------------------
PIN, F_IN, FLO, BW = -14.0, 200e6, 3.3e9, 100e6
F_OUT = FLO + F_IN                                  # the IQ modulator's upper sideband
CONV, NF_MIX, IIP3_MIX, PLO, GIMB, PIMB, ISO = -6.0, 12.0, 15.0, 7.0, 0.2, 1.5, 50.0
IL1, G_DRV, NF_DRV, OIP3_DRV = 1.5, 20.0, 4.0, 32.0
G_PA, NF_PA, PSAT_PA, P_PA, PDC = 30.0, 6.0, 37.0, 2.0, 10.0
IL2 = 0.6
RX_OFFSET, RX_BW = 200e6, 20e6


def rapp_out(pin, g=G_PA, psat=PSAT_PA, p=P_PA):
    s = lin(pin - (psat - g))
    return pin + g + 20 * math.log10((1 + s ** p) ** (-1 / (2 * p)))


def saleh(pin, g=G_PA, psat=PSAT_PA, phisat=10.0, phimax=20.0):
    v = lin(pin) / (lin(psat - g) * 4)
    b = phisat / (phimax - phisat)
    a = math.radians(phimax * b)
    f = cmath.rect(1 / (1 + v), a * v / (1 + b * v))
    return pin + g + 20 * math.log10(abs(f)), math.degrees(cmath.phase(f))


def tanh_db(pin, iip3):
    u = 2 * math.sqrt(lin(pin - iip3))
    n, h, s = 4000, (math.pi / 2) / 4000, 0.0
    for i in range(n + 1):
        t = i * h
        s += (1 if i in (0, n) else 4 if i % 2 else 2) * math.tanh(u * math.cos(t)) * math.cos(t)
    return 20 * math.log10(4 / math.pi * s * h / 3 / u)


def cheb(n, x):
    return math.cos(n * math.acos(x)) if abs(x) <= 1 else math.copysign(1, x) ** n * math.cosh(n * math.acosh(abs(x)))


def bpf(f, f1, f2, n, il, kind, ripple=0.1):
    """|S21|^2 with the flat loss, and |S22|^2 of the lossless prototype."""
    f0 = math.sqrt(f1 * f2)
    x = (f / f0 - f0 / f) / ((f2 - f1) / f0)
    s21 = 1 / (1 + x ** (2 * n)) if kind == "Butterworth" else 1 / (1 + (lin(ripple) - 1) * cheb(n, x) ** 2)
    return s21 * lin(-il), 1 - s21


def main():
    ref = {}
    # The laws: closed form against root-finding the AM-AM.
    ip1_rapp = PSAT_PA - G_PA + 10 / P_PA * math.log10(10 ** (P_PA / 10) - 1)
    ref["rapp_ip1db_closed_dBm"] = ip1_rapp
    ref["rapp_ip1db_root_dBm"] = bisect(lambda x: rapp_out(x) - (x + G_PA - 1), -40, 40)
    ref["rapp_psat_at_80dBm_in_dBm"] = rapp_out(80)
    iip3_rapp = ip1_rapp - db(1 - 10 ** (-1 / 20))
    ssat = PSAT_PA - G_PA + db(4)
    ref["saleh_ip1db_closed_dBm"] = ssat + db(10 ** 0.05 - 1)
    ref["saleh_ip1db_root_dBm"] = bisect(lambda x: saleh(x)[0] - (x + G_PA - 1), -40, 40)
    ref["saleh_psat_peak_dBm"] = max(saleh(ssat + d / 1000)[0] for d in range(-2000, 2001))
    a = math.radians(20.0)
    ref["saleh_iip3_closed_dBm"] = ssat - 5 * math.log10(1 + a * a)
    ref["tanh_ip1db_below_iip3_dB"] = -bisect(lambda x: tanh_db(x, 0.0) + 1, -30, 10)

    # The cascade.
    g_iq = CONV + 20 * math.log10(abs(1 + cmath.rect(10 ** (GIMB / 20), math.radians(PIMB))) / 2)
    stages = [(g_iq, NF_MIX, IIP3_MIX + db(2), IIP3_MIX + db(2) - 8.9625), (-IL1, IL1, None, None),
              (G_DRV, NF_DRV, OIP3_DRV - G_DRV, OIP3_DRV - G_DRV - 8.9625), (G_PA, NF_PA, iip3_rapp, ip1_rapp),
              (-IL2, IL2, None, None)]
    g, te, inv3, inv1 = 1.0, 0.0, 0.0, 0.0
    for gs, nf, iip3, ip1 in stages:
        te += T0 * (lin(nf) - 1) / g
        if iip3 is not None:
            inv3 += g / lin(iip3)
            inv1 += g / lin(ip1)
        g *= lin(gs)
    gain = db(g)
    pout = PIN + gain
    oip3 = -db(inv3) + gain
    op1 = -db(inv1) + gain - 1
    # The output's ceiling, stage by stage: P_k = min(P_(k-1) + g_k, a stage's own), a tanh stage's OIP3 - 10 log10(pi^2/4).
    tanh_sat = db(math.pi ** 2 / 4)
    psat = IIP3_MIX + db(2) + g_iq - tanh_sat - IL1
    psat = min(psat + G_DRV, OIP3_DRV - tanh_sat)
    psat = min(psat + G_PA, PSAT_PA) - IL2
    cw = PIN + g_iq + tanh_db(PIN, IIP3_MIX + db(2)) - IL1
    cw = cw + G_DRV + tanh_db(cw, OIP3_DRV - G_DRV)
    cw = rapp_out(cw) - IL2
    ref.update(gain_dB=gain, nf_dB=db(1 + te / T0), pout_dBm=pout, pout_cw_dBm=cw, op1db_dBm=op1, backoff_op1db_dB=op1 - pout,
               psat_dBm=psat, backoff_psat_dB=psat - pout, oip3_dBm=oip3, aclr_dBc=2 * (pout - oip3) - db(3),
               noise_psd_dBmHz=db(K * (T0 + te) * g) + 30)

    # The receive band.
    t = (T0 + T0 * (lin(NF_MIX) - 1)) * lin(g_iq)
    f_rx = F_OUT + RX_OFFSET
    s21, s22 = bpf(f_rx, 3.4e9, 3.6e9, 5, IL1, "Butterworth")
    t = t * s21 + T0 * max(0, 1 - s21 - s22)
    t = (t + T0 * (lin(NF_DRV) - 1)) * lin(G_DRV)
    t = (t + T0 * (lin(NF_PA) - 1)) * lin(G_PA)
    s21, s22 = bpf(f_rx, 3.4e9, 3.6e9, 5, IL2, "Chebyshev")
    t = t * s21 + T0 * max(0, 1 - s21 - s22)
    ref["rx_noise_psd_dBmHz"] = db(K * t) + 30
    ref["rx_noise_dBm"] = ref["rx_noise_psd_dBmHz"] + db(RX_BW)

    # The IQ modulator's image and LO, at its output and carried through the filters.
    ge = cmath.rect(10 ** (GIMB / 20), math.radians(PIMB))
    image = 20 * math.log10(abs(1 - ge) / 2)
    lo = PLO - ISO + db(1 - math.sin(math.radians(PIMB)))
    p_iq = PIN + g_iq
    ref["image_rejection_dB"] = db(abs(1 + ge) ** 2 / abs(1 - ge) ** 2)
    ref["lo_at_modulator_dBc"] = lo - p_iq

    def through(level, f):
        a1, _ = bpf(f, 3.4e9, 3.6e9, 5, IL1, "Butterworth")
        a2, _ = bpf(f, 3.4e9, 3.6e9, 5, IL2, "Chebyshev")
        return level + db(a1) + G_DRV + G_PA + db(a2)
    ref["image_dBc"] = through(PIN + CONV + image, FLO - F_IN) - pout
    ref["lo_dBc"] = through(lo, FLO) - pout

    # The PA's efficiency at the operating point, on its stated DC power.
    pa_in = PIN + g_iq - IL1 + G_DRV
    pa_out = rapp_out(pa_in + tanh_db(pa_in - G_DRV, OIP3_DRV - G_DRV))
    ref["pa_pae_pct"] = (w(pa_out) - w(pa_in)) / PDC * 100

    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "tx_reference.json")
    with open(path, "w") as f:
        json.dump(ref, f, indent=2)
        f.write("\n")
    for k, v in ref.items():
        print(f"{k:28s} {v: .9f}")


if __name__ == "__main__":
    main()
