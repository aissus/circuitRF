#!/usr/bin/env python3
"""An independent calculation of the example's digital and polar transmitters, for the test suite to compare with.

    python3 stream_reference.py          # writes stream_reference.json beside this file

Standard library only. The chains are examples/RF Chain Budget/Digital TX.cchain and Polar TX.cchain, their numbers
written out below; each readout comes from its textbook form:

    DAC   SQNR = 6.02 N + 1.76 - BO over Fs, BO = 20 log10(FS / x_rms), x_rms = sqrt(2 * 50 * P); + 10 log10(Fs / B) in B
          (Bennett, 1948), and the same floor as a noise of PSD LSB^2 / (12 * 50 * Fs); the hold |H| = |sinc(f / Fs)|,
          its droop across the band as EVM, and its first image at Fs - B/2 against the band's edge, (B/2) / (Fs - B/2)
    polar the mismatch's residual after the delay fit, pi B dT / sqrt(12) over the band (a complex Gaussian with a flat
          spectrum of width B), EtaIn of its power in the channel and EtaAdj in each adjacent one; both by a Monte
          Carlo of that model here, with a radix-2 FFT. The bandwidth's leading-order model: the envelope's AC part
          (pi/16) P over a triangle of +-B, times |1 - H(f) e^(j 2 pi f Tg)|^2, spread as the signal.
          With Vsat, the mean of (Ks Vsat tanh(r/Vsat))^2 over a Rayleigh r.
    Friis on the gains; 1/IIP3 = sum of G_(i-1)/IIP3_i; a Rapp stage's intercept the cubic's at its 1 dB point;
    ACLR = 2 (Pout - OIP3) - 10 log10(3), plus the stream's share in the adjacent channel
"""
import cmath
import json
import math
import os
import random

K, T0, R = 1.380649e-23, 290.0, 50.0


def lin(db): return 10 ** (db / 10)
def db(x): return 10 * math.log10(x)
def w(dbm): return 1e-3 * lin(dbm)


def cheb(n, x): return math.cos(n * math.acos(x)) if abs(x) <= 1 else math.cosh(n * math.acosh(abs(x)))


def bpf(f, f1, f2, n, il, ripple=0.1):
    """A Chebyshev band-pass's |S21|^2 with its flat loss."""
    f0 = math.sqrt(f1 * f2)
    x = (f / f0 - f0 / f) / ((f2 - f1) / f0)
    return lin(-il) / (1 + (lin(ripple) - 1) * cheb(n, x) ** 2)


def butter_lp(f, fc, n, il): return lin(-il) / (1 + (f / fc) ** (2 * n))


def hold(f, fs): return 1.0 if f == 0 else abs(math.sin(math.pi * f / fs) / (math.pi * f / fs))


# ---- the digital transmitter ----------------------------------------------------------------------------------------
PIN, B = -10.0, 19.08e6
BITS, FS_V, FS = 6, 0.32, 122.88e6
LPF_FC, LPF_N, LPF_IL = 49.152e6, 5, 0.5
CONV, NF_MIX, IIP3_MIX, FLO = -7.0, 12.0, 25.0, 3.5e9
G_DRV, NF_DRV, OIP3_DRV = 20.0, 5.0, 38.0
G_PA, NF_PA, PSAT_PA, P_PA = 22.0, 6.0, 43.0, 2.0
BPF_IL = 0.6

# ---- the polar transmitter ------------------------------------------------------------------------------------------
P_POL, KS, FAM, ORDER, DT, F_POL = 0.0, 10.0, 80e6, 3, 1e-9, 3.5e9


def fft(a, inverse=False):
    """Iterative radix-2 FFT of a list of complex numbers, in place; unnormalised."""
    n = len(a)
    j = 0
    for i in range(1, n):
        bit = n >> 1
        while j & bit:
            j ^= bit
            bit >>= 1
        j |= bit
        if i < j: a[i], a[j] = a[j], a[i]
    size = 2
    sign = 1 if inverse else -1
    while size <= n:
        wn = cmath.exp(sign * 2j * math.pi / size)
        for start in range(0, n, size):
            wk = 1
            for k in range(size // 2):
                u, v = a[start + k], a[start + k + size // 2] * wk
                a[start + k], a[start + k + size // 2] = u + v, u - v
                wk *= wn
        size *= 2
    return a


def eta_monte_carlo(seeds=8, n=2 ** 14, over=8):
    """The mismatch residual's in-band and adjacent shares for a complex Gaussian of flat spectrum B, after the
    demodulator's gain and delay fit, at a small dT (first order)."""
    fsr = over * B
    freqs = [(k if k < n // 2 else k - n) * fsr / n for k in range(n)]
    inb = [abs(f) <= B / 2 for f in freqs]
    adj = [B / 2 < f <= 1.5 * B for f in freqs]
    tau = 0.05 / B
    sin_, sadj = 0.0, 0.0
    for seed in range(seeds):
        rng = random.Random(seed)
        X = [complex(rng.gauss(0, 1), rng.gauss(0, 1)) if inb[k] else 0j for k in range(n)]
        x = fft(list(X), inverse=True)
        p = sum(abs(v) ** 2 for v in x) / n
        x = [v / math.sqrt(p) for v in x]
        a = [abs(v) for v in x]
        A = fft([complex(v) for v in a])
        ad = fft([A[k] * cmath.exp(-2j * math.pi * freqs[k] * tau) for k in range(n)], inverse=True)
        e = [ad[k] / n * x[k] / a[k] - x[k] for k in range(n)]
        E = fft(e)
        Xs = fft(list(x))
        D = [2j * math.pi * freqs[k] * Xs[k] for k in range(n)]
        # The fit, in band: E ~ c1 X + c2 D, least squares.
        idx = [k for k in range(n) if inb[k]]
        sxx = sum(abs(Xs[k]) ** 2 for k in idx)
        sdd = sum(abs(D[k]) ** 2 for k in idx)
        sxd = sum(Xs[k].conjugate() * D[k] for k in idx)
        sxe = sum(Xs[k].conjugate() * E[k] for k in idx)
        sde = sum(D[k].conjugate() * E[k] for k in idx)
        det = sxx * sdd - abs(sxd) ** 2
        c1 = (sdd * sxe - sxd * sde) / det
        c2 = (sxx * sde - sxd.conjugate() * sxe) / det
        resid = sum(abs(E[k] - c1 * Xs[k] - c2 * D[k]) ** 2 for k in idx)
        ptot = sum(abs(Xs[k]) ** 2 for k in range(n))
        whole = (math.pi * B * tau) ** 2 / 12
        sin_ += resid / ptot / whole
        sadj += sum(abs(E[k]) ** 2 for k in range(n) if adj[k]) / ptot / whole
    return sin_ / seeds, sadj / seeds


def butter_error(f, fam, n):
    s = complex(0, f / fam)
    h = 1
    for k in range(n):
        p = cmath.exp(1j * math.pi * (2 * k + n + 1) / (2 * n))
        h *= -p / (s - p)
    tg = sum(math.sin((2 * k + 1) * math.pi / (2 * n)) for k in range(n)) / (2 * math.pi * fam)
    return 1 - h * cmath.exp(2j * math.pi * f * tg)


def main():
    ref = {}

    # The DAC, two ways: the textbook SQNR, and the floor as a noise in the cascade.
    x_rms = math.sqrt(2 * R * w(PIN))
    bo = 20 * math.log10(FS_V / x_rms)
    sqnr = 20 * BITS * math.log10(2) + 10 * math.log10(1.5) - bo          # 6.02 N + 1.76 - BO
    pg = db(FS / B)
    lsb = 2 * FS_V / 2 ** BITS
    psd = lsb ** 2 / (12 * R * FS)
    ref["dac_backoff_dB"] = bo
    ref["dac_sqnr_dB"] = sqnr
    ref["dac_sqnr_textbook_dB"] = 6.02 * BITS + 1.76 - bo
    ref["dac_processing_gain_dB"] = pg
    ref["dac_snr_in_band_dB"] = sqnr + pg
    ref["dac_snr_from_its_noise_dB"] = PIN - (db(psd * B) + 30)
    te_dac = psd / K
    ref["dac_nf_dB"] = db(1 + te_dac / T0)
    # The droop across the band as EVM: |H| over [-B/2, B/2], its spread over its mean (midpoint rule), and the
    # small-B closed form (pi^2 / (6 sqrt(180))) (B / Fs)^2.
    m = 20000
    hs = [hold(abs(-B / 2 + (i + 0.5) * B / m), FS) for i in range(m)]
    mean = sum(hs) / m
    ref["dac_droop_evm_pct"] = 100 * math.sqrt(sum(h * h for h in hs) / m - mean * mean) / mean
    ref["dac_droop_evm_closed_pct"] = 100 * math.pi ** 2 / (6 * math.sqrt(180)) * (B / FS) ** 2
    edge = B / 2
    ref["dac_image_at_dac_dB"] = 20 * math.log10(hold(FS - edge, FS) / hold(edge, FS))
    ref["dac_image_closed_dB"] = 20 * math.log10(edge / (FS - edge))

    # The cascade: the DAC (its floor as a temperature), the filters per rail, the IQ modulator fed by the rails (3.01 dB
    # over one mixer's conversion, each mixer at its own rail), the driver, the Rapp PA, the band filter.
    g_iq = CONV + db(2)
    ip1_rapp = PSAT_PA - G_PA + 10 / P_PA * math.log10(10 ** (P_PA / 10) - 1)
    iip3_rapp = ip1_rapp - db(1 - 10 ** (-1 / 20))
    stages = [(0.0, db(1 + te_dac / T0), None), (-LPF_IL, LPF_IL, None), (g_iq, NF_MIX, IIP3_MIX),
              (G_DRV, NF_DRV, OIP3_DRV - G_DRV), (G_PA, NF_PA, iip3_rapp), (-BPF_IL, BPF_IL, None)]
    g, te, inv3 = 1.0, 0.0, 0.0
    for gs, nf, iip3 in stages:
        te += T0 * (lin(nf) - 1) / g
        if iip3 is not None: inv3 += g / lin(iip3)
        g *= lin(gs)
    gain = db(g)
    pout = PIN + gain
    oip3 = -db(inv3) + gain
    snr = PIN - (db(K * te * B) + 30)                     # the source at 0 K: a stream
    aclr_dist = 2 * (pout - oip3) - db(3)
    # The DAC's floor in the adjacent channel: its noise in B, held at B over 0 Hz, through the low-pass at B and the
    # band filter at FLO + B, each against the carrier.
    n_dac_out = db(K * te_dac * B) + 30
    rel_adj = (20 * math.log10(hold(B, FS)) + db(butter_lp(B, LPF_FC, LPF_N, LPF_IL) / butter_lp(0, LPF_FC, LPF_N, LPF_IL))
               + db(bpf(FLO + B, 3.4e9, 3.6e9, 5, BPF_IL) / bpf(FLO, 3.4e9, 3.6e9, 5, BPF_IL)))
    adj = lin(n_dac_out + rel_adj - PIN)
    f_img = FS - edge
    img = (ref["dac_image_at_dac_dB"] + db(butter_lp(f_img, LPF_FC, LPF_N, LPF_IL) / butter_lp(0, LPF_FC, LPF_N, LPF_IL))
           + db(bpf(FLO + f_img, 3.4e9, 3.6e9, 5, BPF_IL) / bpf(FLO, 3.4e9, 3.6e9, 5, BPF_IL)))
    ref["digital_gain_dB"] = gain
    ref["digital_pout_dBm"] = pout
    ref["digital_oip3_dBm"] = oip3
    ref["digital_snr_dB"] = snr
    ref["digital_aclr_dist_dBc"] = aclr_dist
    ref["digital_aclr_dBc"] = db(lin(aclr_dist) + adj)
    ref["digital_evm_floor_pct"] = 100 * math.sqrt(lin(-snr) + (ref["dac_droop_evm_pct"] / 100) ** 2)
    ref["digital_dac_image_dBc"] = img

    # The polar transmitter.
    whole = math.pi * B * DT / math.sqrt(12)
    eta_in, eta_adj = 0.436, 0.215
    ref["polar_mismatch_whole_band_pct"] = 100 * whole
    ref["polar_mismatch_evm_pct"] = 100 * math.sqrt(eta_in) * whole
    tri = lambda f: max(0.0, 1 - abs(f) / B)
    mm = 40000
    h = 2 * B / mm
    e_in = e_adj = 0.0
    for i in range(mm):
        f = -B + (i + 0.5) * h
        s = math.pi / 16 / B * tri(f) * abs(butter_error(f, FAM, ORDER)) ** 2
        e_in += s * tri(f) * h
        e_adj += s * tri(f - B) * h
    bpf_adj = db(bpf(F_POL + B, 3.4e9, 3.6e9, 5, BPF_IL) / bpf(F_POL, 3.4e9, 3.6e9, 5, BPF_IL))
    ref["polar_bandwidth_evm_pct"] = 100 * math.sqrt(e_in)
    ref["polar_bandwidth_adj_dBc"] = db(e_adj)
    ref["polar_pout_dBm"] = P_POL + 20 * math.log10(KS) - BPF_IL
    ref["polar_evm_floor_pct"] = 100 * math.sqrt(eta_in * whole ** 2 + e_in)   # the source at 0 K: no noise
    ref["polar_aclr_dBc"] = db((eta_adj * whole ** 2 + e_adj) * lin(bpf_adj))
    # With Vsat, the mean output over a Rayleigh envelope, by the midpoint rule over r: Ks = 10, Vsat = 1 V, 0 dBm in.
    s2 = 2 * R * w(0.0)
    mr = 200000
    rmax = 8 * math.sqrt(s2)
    acc = 0.0
    for i in range(mr):
        r = (i + 0.5) * rmax / mr
        acc += (10 * math.tanh(r / 1.0)) ** 2 * (2 * r / s2) * math.exp(-r * r / s2) * rmax / mr
    ref["polar_vsat_gain_dB"] = db(acc / s2)
    ref["eta_in_monte_carlo"], ref["eta_adj_monte_carlo"] = eta_monte_carlo()

    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "stream_reference.json")
    with open(path, "w") as f:
        json.dump(ref, f, indent=2)
        f.write("\n")
    for k, v in ref.items():
        print(f"{k:32s} {v: .9f}")


if __name__ == "__main__":
    main()
