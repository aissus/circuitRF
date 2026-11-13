#!/usr/bin/env python3
"""An independent calculation of the chain budget's arrays, for the test suite to compare with.

    python3 arrays_reference.py              # writes arrays_reference.json beside this file

Standard library only. The budget follows a combiner's paths node by node (CombinerSpec.Budget); this one sums the
same network SOURCE BY SOURCE instead, each to the output through its own path:

    signal    G_s = (sum over elements of t_k)^2, t_k the product of its weights, each part's sqrt(g/n), each amp's sqrt(G)
    noise     G_n = sum of t_k^2; T_out = sum over every pad, part and amplifier of its own noise times its power gain to
              the output: a pad of gain a^2 at T0 adds T0 (1 - a^2), a part of loss 1/g adds T0 (1 - g) (Bosma on its S),
              an amplifier T0 (F - 1) at its input; Te = T_out / G_n
    IM3       each amplifier's third-order product, in phase: its input amplitude s cubed over its IIP3, times the
              amplitude gain from its input to the output; IIP3 = sum t_k / sum of those. IP1dB likewise from IIP3 - 8.9625
    cascade   Friis on the noise gains, 1/IIP3 = sum G_(i-1)/IIP3_i on the signal gains, array gain = prod G_s/G_n

A tree's level l of ways w over m inputs has ceil(m/w) parts, the last of the remainder; a part of one input passes
through, with no loss, and (active) still has its level's amplifier. The designs are the example's IF Array
(examples/RF Chain Budget/IF Array.cchain) and three combiners on their own: a 2:1-then-3:1 active tree with a taper, a
2:1 tree of five with pass-throughs, and the example's sub-array alone.
"""
import json
import math
import os

K, T0 = 1.380649e-23, 290.0
TANH_P1 = 8.9625


def lin(db): return 10 ** (db / 10)
def db(x): return 10 * math.log10(x)


def tree(n, ways, il, path=None, active=None):
    """A combiner's levels as parts: [[(level, part, [element or part indices of the level below])...]...]."""
    levels, m = [], n
    for w in ways:
        parts = [list(range(j, min(j + w, m))) for j in range(0, m, w)]
        levels.append(parts)
        m = len(parts)
    assert m == 1
    return levels


def combiner(n, ways, il, path=None, active=None):
    """The combiner's stage budget, source by source. il: each level's loss; active: each level's (G, NF, IIP3)."""
    levels = tree(n, ways, il)
    a = [10 ** (-(path[k] if path else 0) / 20) for k in range(n)]
    # The amplitude gain from each node's output to the combiner's output: walk down from the top.
    # node (l, j): level l's part j output (after its amplifier when active); l = -1: element k after its pad.
    down = {}
    def gain_down(l, j):
        """Amplitude gain from node (l, j) to the output."""
        if (l, j) in down: return down[(l, j)]
        if l == len(levels) - 1:
            v = 1.0
        else:
            parent = next(p for p, members in enumerate(levels[l + 1]) if j in members)
            size = len(levels[l + 1][parent])
            g = lin(-il[l + 1])
            v = (math.sqrt(g / size) if size > 1 else 1.0) * (math.sqrt(lin(active[l + 1][0])) if active else 1.0) \
                * gain_down(l + 1, parent)
        down[(l, j)] = v
        return v
    # The amplitude from each element to the input of node (l, j)'s combiner part, and the coherent sum there.
    def up(l, j):
        """Coherent amplitude at node (l, j)'s part input sum, before its amplifier, per unit element amplitude."""
        members = levels[l][j]
        size = len(members)
        g = lin(-il[l])
        f = math.sqrt(g / size) if size > 1 else 1.0
        if l == 0:
            return f * sum(a[k] for k in members)
        return f * sum(out(l - 1, i) for i in members)
    def out(l, j):
        return up(l, j) * (math.sqrt(lin(active[l][0])) if active else 1.0)
    # Each element's own amplitude to the output.
    t = []
    for k in range(n):
        j0 = next(j for j, members in enumerate(levels[0]) if k in members)
        size = len(levels[0][j0])
        g = lin(-il[0])
        v = a[k] * (math.sqrt(g / size) if size > 1 else 1.0) * (math.sqrt(lin(active[0][0])) if active else 1.0) * gain_down(0, j0)
        t.append(v)
    gs = sum(t) ** 2
    gn = sum(x * x for x in t)
    # Noise, source by source, each at its own node, times its power gain to the output.
    tout = 0.0
    for k in range(n):   # the pads, each referred to its element's own path
        tout += T0 * (1 - a[k] ** 2) * (t[k] / a[k]) ** 2
    q3 = q1 = 0.0
    for l, parts in enumerate(levels):
        for j, members in enumerate(parts):
            g = lin(-il[l])
            ga = lin(active[l][0]) if active else 1.0
            if len(members) > 1:   # the part's own noise at its output, then its amplifier
                tout += T0 * (1 - g) * ga * gain_down(l, j) ** 2
            if active:
                G, nf, ip3 = active[l]
                tout += T0 * (lin(nf) - 1) * ga * gain_down(l, j) ** 2
                s = up(l, j)
                q3 += s ** 3 / lin(ip3) * math.sqrt(ga) * gain_down(l, j)
                q1 += s ** 3 / lin(ip3 - TANH_P1) * math.sqrt(ga) * gain_down(l, j)
    te = tout / gn
    s = sum(t)
    return dict(G=db(gs), Gn=db(gn), NF=db(1 + te / T0),
                IIP3=db(s / q3) if q3 > 0 else None, IP1=db(s / q1) if q1 > 0 else None, A=db(gs / gn), N=n)


def stage(g, nf, iip3=None):
    return dict(G=g, Gn=g, NF=nf, IIP3=iip3, IP1=None if iip3 is None else iip3 - TANH_P1, A=0.0)


def cascade(stages, pin, ts=290.0, bw=4.5e6):
    gs = gn = 1.0
    te = inv3 = inv1 = 0.0
    a = 1.0
    rows = []
    for name, s in stages:
        te += T0 * (lin(s["NF"]) - 1) / gn
        if s["IIP3"] is not None: inv3 += gs / lin(s["IIP3"])
        if s["IP1"] is not None: inv1 += gs / lin(s["IP1"])
        gs *= lin(s["G"]); gn *= lin(s["Gn"]); a *= lin(s["G"] - s["Gn"])
        mds = db(K * (ts + te) * bw) + 30 - db(a)
        rows.append(dict(Name=name, G=db(gs), NF=db(1 + te / T0), IIP3=-db(inv3) if inv3 > 0 else None,
                         IP1=-db(inv1) if inv1 > 0 else None, A=db(a), SNR=pin - mds,
                         StageG=s["G"], StageGn=s["Gn"], StageNF=s["NF"], StageIIP3=s["IIP3"]))
    return rows


# ---- the example's IF Array: per element LNA, preselector, mixer (common LO), IF filter and an IF phase shifter; three into an
# active sub-array tree (2:1 and a pass-through, then 2:1) with a taper; a sub-array amplifier; two sub-arrays into one.
SUB = dict(n=3, ways=[2, 2], il=[0.5, 0.5], path=[1, 0, 1], active=[(10, 2, 10), (10, 2, 10)])
IF_ARRAY = [
    ("LNA", stage(18, 1.2, 10 - 18)),           # IP3 10 dBm at its output
    ("RFBPF", stage(-1.5, 1.5)),
    ("MIX", stage(-7, 7, 10)),
    ("IFBPF", stage(-1, 1)),
    ("PS", stage(-3, 3, 15)),
    ("SUB", combiner(**SUB)),
    ("SA", stage(10, 3, 25 - 10)),
    ("ARR", combiner(n=2, ways=[2], il=[0.3])),
    ("IFA", stage(10, 4, 30 - 10)),
]

COMBINERS = {
    # Mixed ways: three 2:1 parts, then one 3:1; active, each level its own amplifier; a taper over the six.
    "Mixed": combiner(n=6, ways=[2, 3], il=[0.5, 0.3], path=[2, 1, 0, 0, 1, 2], active=[(10, 2, 10), (8, 3, 16)]),
    # Five elements in 2:1 levels: 5 -> 3 -> 2 -> 1, a pass-through at the first two levels; passive.
    "Five": combiner(n=5, ways=[2, 2, 2], il=[0.5, 0.5, 0.5]),
    "Sub": combiner(**SUB),
}

out = {
    "IfArray": cascade(IF_ARRAY, pin=-80.0),
    "Combiners": COMBINERS,
}

here = os.path.dirname(os.path.abspath(__file__))
with open(os.path.join(here, "arrays_reference.json"), "w") as fh:
    json.dump(out, fh, indent=1)
    fh.write("\n")

if __name__ == "__main__":
    # Equal paths reduce to the chain's own cascade of its levels: a 2:1 tree of four, active, by Friis by hand.
    c = combiner(n=4, ways=[2, 2], il=[0.5, 0.3], active=[(10, 2, 10), (8, 3, 16)])
    gs, gn, te, inv3 = 1.0, 1.0, 0.0, 0.0
    for il, (g, nf, ip) in zip([0.5, 0.3], [(10, 2, 10), (8, 3, 16)]):
        gl = lin(-il)
        te += T0 * (1 / gl - 1) / gn; gs *= 2 * gl; gn *= gl
        te += T0 * (lin(nf) - 1) / gn; inv3 += gs / lin(ip); gs *= lin(g); gn *= lin(g)
    assert abs(c["G"] - db(gs)) < 1e-12 and abs(c["NF"] - db(1 + te / T0)) < 1e-12 and abs(c["IIP3"] + db(inv3)) < 1e-12
    # A lossless pass-through tree keeps every element's noise (G_n = 1) and loses only the coherence.
    f = combiner(n=3, ways=[2, 2], il=[0, 0])
    assert abs(f["Gn"]) < 1e-12 and abs(f["NF"]) < 1e-12 and abs(f["G"] - db((1 + 1 / math.sqrt(2)) ** 2)) < 1e-12
    for name, c in COMBINERS.items():
        print(name, {k: round(v, 6) if isinstance(v, float) else v for k, v in c.items()})
    for r in out["IfArray"]:
        print(r["Name"], round(r["G"], 6), round(r["NF"], 6), r["IIP3"] and round(r["IIP3"], 6), round(r["A"], 6))
