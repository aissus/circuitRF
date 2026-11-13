#!/usr/bin/env python3
"""Regenerates the wideband receiver's two Touchstone files, illustrative data of this example's own.

    python3 "examples/RF Chain Budget/Wideband.gen.py" [out-dir]      # beside this script when no folder is given

Standard library only. It writes the two files and nothing else; Wideband.cchain and its benches are authored by the
RF Chain Budget window and its Transfer to Schematic.

- `Wideband preselector.s2p`: a 3rd-order 0.5 dB Chebyshev band filter for 2.2 to 2.6 GHz, built of LC resonators of
  Q 150 (series, shunt, series) between 50 ohm and computed by their ABCD product, 0.5 to 5 GHz in 10 MHz steps. Its
  ports reflect, -12.3 dB at 2.45 GHz, as a measured filter's do.
- `Wideband LNA.s2p`: an amplifier module, 17 dB, its ports reflecting -9.1 and -9.9 dB, with noise data (NFmin
  0.7 dB, |Gopt| 0.3, rn 0.18), each quantity a straight line in frequency from 0.5 to 6 GHz.
"""
import cmath
import math
import os
import sys

Z0 = 50.0


def gvals(n, ripple_db):
    """A Chebyshev lowpass prototype's element values (Pozar, Microwave Engineering, 4th ed., 8.3)."""
    beta = math.log(1 / math.tanh(ripple_db / 17.37))
    gam = math.sinh(beta / (2 * n))
    a = [math.sin((2 * k - 1) * math.pi / (2 * n)) for k in range(1, n + 1)]
    b = [gam ** 2 + math.sin(k * math.pi / n) ** 2 for k in range(1, n + 1)]
    g = [2 * a[0] / gam]
    for k in range(1, n):
        g.append(4 * a[k - 1] * a[k] / (b[k - 1] * g[k - 1]))
    return g


F1, F2, Q = 2.2e9, 2.6e9, 150.0
W0 = 2 * math.pi * math.sqrt(F1 * F2)
FBW = (F2 - F1) / math.sqrt(F1 * F2)
G = gvals(3, 0.5)


def mul(a, b):
    return [[a[0][0] * b[0][0] + a[0][1] * b[1][0], a[0][0] * b[0][1] + a[0][1] * b[1][1]],
            [a[1][0] * b[0][0] + a[1][1] * b[1][0], a[1][0] * b[0][1] + a[1][1] * b[1][1]]]


def preselector(f):
    """S11, S12, S21, S22 of the band filter at f."""
    w = 2 * math.pi * f
    m = [[1, 0], [0, 1]]
    for k, gk in enumerate(G):
        if k % 2 == 0:   # a series resonator
            l, c = gk * Z0 / (W0 * FBW), FBW / (W0 * gk * Z0)
            m = mul(m, [[1, complex(W0 * l / Q, w * l - 1 / (w * c))], [0, 1]])
        else:            # a shunt one
            c, l = gk / (W0 * FBW * Z0), FBW * Z0 / (W0 * gk)
            m = mul(m, [[1, 0], [complex(W0 * c / Q, w * c - 1 / (w * l)), 1]])
    (a, b), (c, d) = m
    den = a + b / Z0 + c * Z0 + d
    return [(a + b / Z0 - c * Z0 - d) / den, 2 * (a * d - b * c) / den, 2 / den, (-a + b / Z0 - c * Z0 + d) / den]


def lna(f):
    """S11, S21, S12, S22 of the amplifier module at f."""
    x = (f - 2.45e9) / 1e9
    r = lambda m, deg: cmath.rect(m, math.radians(deg))
    return [r(0.35, -60 - 30 * x), r(7.0 * (1 - 0.05 * x), 100 - 60 * x), r(0.04, 30 - 20 * x), r(0.32, -50 - 25 * x)]


def ma(z):
    return f"{abs(z):.9g} {math.degrees(cmath.phase(z)):.9g}"


def main(out):
    with open(os.path.join(out, "Wideband preselector.s2p"), "w") as fh:
        fh.write("! Illustrative: a 3rd-order 0.5 dB Chebyshev band filter for 2.2 to 2.6 GHz, LC resonators of Q 150\n# GHz S MA R 50\n")
        for i in range(0, 451):
            f = 0.5e9 + i * 10e6
            s11, s12, s21, s22 = preselector(f)
            fh.write(f"{f/1e9:.3f} {ma(s11)} {ma(s21)} {ma(s12)} {ma(s22)}\n")
    with open(os.path.join(out, "Wideband LNA.s2p"), "w") as fh:
        fh.write("! Illustrative: an LNA module, its ports reflecting about -9 and -10 dB, with noise data\n# GHz S MA R 50\n")
        for i in range(0, 23):
            f = 0.5e9 + i * 0.25e9
            s11, s21, s12, s22 = lna(f)
            fh.write(f"{f/1e9:.3f} {ma(s11)} {ma(s21)} {ma(s12)} {ma(s22)}\n")
        for i in range(0, 23):
            f = 0.5e9 + i * 0.25e9
            x = (f - 2.45e9) / 1e9
            fh.write(f"{f/1e9:.3f} {0.7 + 0.1 * x:.6g} 0.3 {40 + 15 * x:.6g} 0.18\n")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__)))
