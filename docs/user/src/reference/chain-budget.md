---
title: RF Chain Budget
slug: reference/chain-budget.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > RF Chain Budget
lede: A receive or transmit chain stage by stage, each stage a System block with its own parameters - gain, noise figure, intercepts, compression, sensitivity and dynamic range in closed form, an N-way array included, or a transmitter's output power, back-off, ACLR and noise floor - and one action that writes the chain as test benches whose simulations put a number beside every one of them.
keywords: chain budget, link budget, cascade, EIRP, free-space path loss, atmospheric attenuation, Shannon capacity, data rate, fractional bandwidth, energy per bit, DC power, PAE, back-off, frequency-scalable, frequency sweep, stage count, NF allowance, n258, receiver, Friis, noise figure, cascaded IP3, IIP3, OIP3, P1dB, sensitivity, MDS, noise floor, SFDR, SNR, phased array, array gain, array factor, beam steering, taper, combiner, combining tree, active combiner, Wilkinson, phase shifter, limiter, splitter, superhet, dual conversion, second IF, image rejection, levels, blocker, IM3, canceller, cancellation depth, EVM, constellation, demodulation, transmit chain, transmitter, up-conversion, sideband, IQ modulator, LO feedthrough, power amplifier, PA, Psat, ACLR, ACPR, AM-AM, AM-PM, Rapp, Saleh, bandpass nonlinearity, receive band, .cchain
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What it is</a></li>
<li><a href="#opening">Opening it</a></li>
<li><a href="#inputs">The system inputs</a></li>
<li><a href="#stages">The stages, and the parts a stage can be</a></li>
<li><a href="#scaling">Values that follow the frequency</a></li>
<li><a href="#cascade">What the cascade computes</a></li>
<li><a href="#transmit">Transmit chains</a></li>
<li><a href="#laws">The amplifier's laws</a></li>
<li><a href="#iqmod">The IQ modulator</a></li>
<li><a href="#array">Arrays: N elements into a combiner</a></li>
<li><a href="#variants">The combiner's four variants</a></li>
<li><a href="#link">A link in front of the chain</a></li>
<li><a href="#readouts">SNR, rate, DC power and the NF allowance</a></li>
<li><a href="#sweeps">Sweeps</a></li>
<li><a href="#transfer">Transfer to Schematic</a></li>
<li><a href="#conversions">More than one conversion</a></li>
<li><a href="#steering">Steering the beam in simulation</a></li>
<li><a href="#levels">Levels at every node</a></li>
<li><a href="#canceller">The canceller</a></li>
<li><a href="#demo">The modulated-signal demo</a></li>
<li><a href="#linear-tx">A linear transmitter</a></li>
<li><a href="#agreement">How closely the simulation agrees</a></li>
<li><a href="#format">The .cchain file</a></li>
<li><a href="#cli">From the command line</a></li>
</ol>
</nav>

## What it is {#what}

A **chain budget** is the spreadsheet every receiver and transmitter starts as: an ordered list of stages, each with a
gain, a noise figure and an intercept, and the cascade worked out stage by stage. The RF Chain Budget is that table, with
two differences that matter here:

- **Every stage is a part circuitRF already has.** An amplifier stage is the System [Amplifier](system-components.html#amp)
  with the Amplifier's own `Gain`, `NF`, `IP3` and `IP3Ref`; a filter stage is the [Filter](system-components.html#filter)
  with its own `Response`, `Form`, band and `IL`. The budget reads each part's numbers the way the part's model behaves.
- **One action turns the chain into a simulation.** **Transfer to Schematic** writes the chain as test benches of
  those same parts, with the analyses that check the budget already set up and the budget's numbers written beside the
  simulated ones. The cascade is closed-form arithmetic; the check is circuitRF's own S-parameter, harmonic-balance and
  envelope analyses.

## Opening it {#opening}

- **Tools ▸ RF Chain Budget** opens a new chain: an LNA, a filter and a second amplifier.
- A `.cchain` in the workspace tree opens on a double-click.
- **File ▸ New ▸ Examples ▸ RF Chain Budget** has a 2.45 GHz superhet, a four-element steered array, a front end
  with a canceller, a dual-conversion receiver, a 26 GHz link and a 3.5 GHz linear transmitter, each already
  transferred.

{{ui: chain-budget}}

The window has three parts, and every edit recomputes all of them at once:

- **The chain**, input on the left, drawn two ways that share one selection:
  - **Blocks**: one idealised block per stage, drawn with its System block's own symbol, with its own gain, NF and
    intercept under it and the cascade at its output (Σ) under that. An array is drawn compactly: the branch's stages
    once, each marked "× N", into one combiner block drawn as its variant — a direct combiner one N:1 symbol, a tree its
    levels of 2:1 (a deep one as "× levels"), an amplifier after each when it is active. A filter's block shows its
    band form. Drag a block left or right to move its stage: a line shows where it will land. **Delete**
    removes the selected block's stage. Right-click for **Insert Block** ▸ a part, which inserts a stage of that part
    where you clicked (the left half of a block before it, the right half after it), **Copy**, which puts the whole
    lineup on the clipboard as a picture to paste into a slide or a document, and **Remove Block**, which removes the
    block you clicked. Tick **Levels** for the [level diagram](#level-diagram) under the blocks, a node on each gap.
  - **Schematic**: the schematic Transfer would write, drawn as every circuitRF schematic is, without the bench's
    sources, analyses and isolators: each stage is its own parts (a composed stage its composition, a tree its levels
    of Combiners), and in an array every element's branch is drawn into the combiner. Above each stage are four lines:
    its own gain and NF, its own intercept and compression point, and the cascade's gain, NF, IIP3 and SNR at its output.
    Scroll to zoom, drag with the middle button to pan. **Z** and a drag zoom to a box, and **F**, the Fit button or
    a double-click fits. Right-click ▸ **Copy** copies it as the schematic editor copies: a picture for a slide or a
    document, and parts and wires to paste into a schematic.

  Clicking a block, or a part in the schematic, selects its stage, and the selected stage is drawn selected in both.
  A click off every block or part, or Escape outside a text box, clears the selection.
- **The stages**, one row each: the name, the part, and the part's parameters. Selecting a stage in either view
  scrolls its row to the top of the list.
- **The cascade, the levels and the canceller**, [Data Display](data-display.html) tabs: the cascade table, one
  row per stage; the [levels at every node](#levels), the budget's lines with the transferred benches' simulated
  points once they have run; the [Link](#link) tab, with the link's and the receiver's inputs and numbers; a tab for
  each [sweep](#sweeps) the chain asks for; and, for a chain with a canceller, [its depth](#canceller) against its
  errors. The chain's totals are below them.

Drag the gap between two of the three parts to give one more height.

**Undo** and **Redo** (the toolbar, or Ctrl+Z and Ctrl+Shift+Z, ⌘ on a Mac) step through every edit: an input, a stage's
name, part or parameter, and every stage added, removed or moved. A value typed into a box is one step. Undoing back
to the chain as it was opened or last saved leaves it unchanged, with nothing to save.

A value the budget cannot use is never taken. While you type one, its box is outlined in red, the reason is under the
chain's totals, and everything else keeps showing the last chain that worked; leave the box (or press Enter) with the
value still refused and it goes back to the last one that worked. A choice from a list that the budget refuses, such
as a tree combiner whose N is not a power of 2, is not made, and the reason is shown.

## The system inputs {#inputs}

| Input | Meaning |
|---|---|
| Frequency | The input (RF) frequency, in the frequency unit set in Settings ▸ General ▸ Units (GHz when that is Automatic). After a mixer the chain is at the difference frequency. |
| Bandwidth | The noise bandwidth B the noise floor, the MDS and the SNR are read in. |
| Input power | The signal at the input. In an array, at **each element**. |
| Source temperature | The noise temperature of what drives the input, an antenna's for instance. 290 K is T0. |
| Required SNR | What the demodulator needs: sensitivity is the MDS plus this. |
| Blockers | Carriers at the input besides the signal: an offset from the frequency (MHz when bare) and a level, dBm, each pair separated by `;` (`150 MHz, -40; 20 MHz, -50`). See [Levels](#levels). |
| Direction | `Receive`, the default, or `Transmit`: a chain driven at its input and read at its output. See [Transmit chains](#transmit). |
| RxOffset, RxBandwidth | A transmit chain's receive band, where its output noise is read: its centre's offset from the carrier at the output and its width, MHz. Blank offset: none. Shown only when Direction is `Transmit`; a receive chain keeps the values. |

**Transfer to Schematic**'s drop-down has **include a modulated-signal demo**, which adds the [demo bench](#demo) to
what it writes.

## The stages, and the parts a stage can be {#stages}

A stage has a name (its instance name on the schematic), a part, and that part's parameters. Ten parts are System
blocks — the splitter and the array combiner are the library's [Combiner](system-components.html#combiner), an
active combiner that block with an Amplifier after each, and the DAC stage is two of the library's
[DAC](system-components.html#dac), one per rail — and the library has no phase shifter, limiter, canceller, IQ
modulator or polar transmitter, so five more are **composed** of parts it does have. Transfer places exactly that
composition. One more is **data**: an [S-parameter data](#snp) stage is a Touchstone file.

| Part | What it is | Budget |
|---|---|---|
| Amplifier (`Amp`) | The System Amplifier. Any gain, negative included: a block with a gain, an NF and an intercept is one. `GainPerStage` makes it ⌈`Gain`/`GainPerStage`⌉ amplifier stages, for its [DC power](#readouts). `Law` picks its compression: `Tanh` on `IP3`, or `Rapp` or `Saleh` on `Psat` ([the laws](#laws)). | G = `Gain`, NF = `NF` (blank: noiseless), IIP3 from `IP3` and `IP3Ref`, IP1dB = IIP3 − 8.96 dB; a Rapp or Saleh law's own |
| Mixer | The System Mixer, down-converting to \|f − `Flo`\|, or with `Sideband` = Upper up-converting to f + `Flo`. `Flo` is the LO the bench drives it with, at `Plo`. | G = `ConvGain`, NF = `NF` single-sideband (a DSB figure + 3.01 dB), IIP3 = `IIP3`, IP1dB = IIP3 − 8.96 dB |
| IQ modulator (`IQMod`, composed) | Two Mixers fed in quadrature by a 90° Hybrid at the input, their LO split into its quadrature by a Combiner, summed by a second: an image-reject up-converter to `Flo` + f (`Sideband` Upper) or `Flo` − f, with `GainImb`, `PhaseImb` and each mixer's `IsoLO_IF`. After a DAC it takes the two rails as its I and Q, with no hybrid. See [The IQ modulator](#iqmod). | G = `ConvGain` + 20·log10(\|1 + g·e^(jφ)\|/2), NF a mixer's, IIP3 = `IIP3` + 3.01 dB; after a DAC, G + 3.01 dB and IIP3 = `IIP3` |
| DAC pair (`DAC`) | Two [DACs](system-components.html#dac), one per rail of the digital stream: `Bits` across ±`FS` at its 50 Ω input, held at `Fs` (`Mode` nrz, or rz for `Duty` of the period), with a `Gain`. The first stage of a transmit chain; its rails run through filters, amplifiers and attenuators, one per rail, into an IQ modulator. See [Digital and polar transmitters](#streams). | G = `Gain` + 20·log10\|H(f)\|, the hold's droop at the carrier's offset; NF = 10·log10(1 + T_q/T0), its quantisation floor as a temperature |
| Polar transmitter (`PolarTx`, composed) | A polar pair recombined in a [Supply-Modulated Amplifier](system-components.html#supplyamp): the amplitude through a Delay of `dT` and a Butterworth lowpass of `Fam` and `Order` into its supply, the phase turning a limited CW carrier in a [Phase Modulator](system-components.html#phasemod), delayed by the lowpass's DC group delay. `Ks` and `Vsat` are the amplifier's. The first stage of a transmit chain. See [Digital and polar transmitters](#streams). | G = 20·log10 `Ks` (with `Vsat`, the tanh law's mean over the envelope); noiseless; its mismatch and bandwidth in the transmit readouts |
| Attenuator (`Atten`) | A matched pad. | G = −`Loss`, NF = `Loss` |
| Filter | The synthesised Filter, in its passband. | G = −`IL`, NF = `IL`; a Chebyshev's ripple is not in the budget |
| Switch | A closed SPST switch. | G = −`IL`, NF = `IL` |
| Isolator (`Circulator`) | A circulator with its third port terminated. | G = −`IL`, NF = `IL` |
| Directional coupler (`Coupler`) | Its through arm, the coupled and isolated ports terminated. | G = 10·log10(1 − 10^(−`Coupling`/10)) − `IL`, NF = −G |
| Phase shifter (composed) | An Amplifier of gain −`IL` with `NF` and `IIP3`, and a TLIN of electrical length `Phase`. | G = −`IL`, NF = `NF` (blank: `IL`), IIP3 = `IIP3` (blank: linear) |
| Limiter (composed) | An Amplifier of gain −`IL`, NF = `IL`, whose intercept puts its input 1 dB compression point at `P1dB`. | G = −`IL`, NF = `IL`, IP1dB = `P1dB` |
| Canceller (composed) | A Directional Coupler whose isolated port takes a reference path that subtracts a copy of an interference from the main path. See [The canceller](#canceller). | G = −(`IL` − 10·log10(1 − c)), NF from the reference path's noise |
| N-way splitter | The [Combiner](system-components.html#combiner) from its common port, one output followed and the others terminated. | G = −(10·log10 N + `IL`), NF = −G |
| S-parameter data (`SnP`) | A Touchstone file on the [SnP](components.html#snp) block, read from port `In` to port `Out`. See [S-parameter data](#snp). | G = \|S(`Out`,`In`)\|² at the stage's frequency; NF from the file's noise data, or its own loss when passive; `NF` and `IIP3` when stated |
| N-way array combiner | The [Combiner](system-components.html#combiner), every input an element's branch: one N:1, or a tree of 2:1s, passive or active. See [Arrays](#array) and [the variants](#variants). | Direct, passive: signal +10·log10 N − `IL`, noise −`IL`, NF = `IL` |

Every part also takes a `Pdc`, its DC power (W, or with its unit: `5 mW`), which only the [power readout](#readouts)
reads; on an Amplifier with `GainPerStage` it is each amplifier stage's. Neither is written onto a bench.

**P1dB follows from the intercept** for every part that compresses, because an ideal Amplifier and Mixer have one
nonlinearity, a tanh limiter, whose 1 dB compression point is IIP3 − 8.9625 dB. An Amplifier with a Rapp or Saleh law
compresses as its law does instead. The budget uses the part's own law, so the simulation it is checked against
compresses where the budget says.

### S-parameter data {#snp}

An **S-parameter data** stage puts a measured or simulated part into the chain as its Touchstone file: a filter, an
LNA, a cable, a board. Pick the file with **…** beside **File**; a file inside the workspace is written relative to it,
so the chain and its benches move with the workspace. Choosing the part asks for the file at once.

The file is interpolated to the frequency the stage sees — the chain's, or after a mixer the IF — exactly as the SnP
block interpolates it in a simulation, and renormalised to 50 Ω if it is referenced to another impedance. A file with
more than two ports is read from `In` to `Out`, every other port terminated in 50 Ω.

| Quantity | From the file |
|---|---|
| Gain | \|S(`Out`,`In`)\|², between 50 Ω terminations |
| NF, from noise data | A two-port read 1 → 2 with a noise block: its Fmin, Γopt and Rn at the stage's frequency (linear between rows, as the block takes them), at a 50 Ω source |
| NF, passive, no noise data | Its own thermal noise: F = (1 − \|S(`Out`,`Out`)\|²) / \|S(`Out`,`In`)\|², its loss when matched |
| NF, with gain and no noise data | Unknown: give `NF` |
| IIP3 | `IIP3`; blank, linear |

`NF` stated replaces the file's figure and may not be below it. On the benches a stated `NF` above the file's own, or a
stated `IIP3`, is an ideal Amplifier of 0 dB after the block that carries the extra noise and the intercept, so the
simulation still checks the stage.

The budget does not extrapolate. It refuses the chain, naming the stage, when:

- no file is named, there is no file at the path, or the file is not Touchstone;
- `In` or `Out` is not one of the file's ports, or both are the same port;
- the stage's frequency is outside the file's frequencies, or outside its noise rows when the noise comes from them
  (state `NF` to use the file there);
- the file has gain and no noise data, and `NF` is blank;
- a relative path is given in a chain that has not been saved.

A blocker that reaches the stage outside the file's frequencies is carried at the file's nearest frequency, as the block
holds it in a simulation, and the Notes say so.

## Values that follow the frequency {#scaling}

A stage parameter that is a number may also be written as a value of the frequency, so a stage scales as the carrier
moves and a [frequency sweep](#sweeps) shows it:

| Written as | Example | Read |
|---|---|---|
| A number | `8.5`, `8.5 dB`, `2.35 GHz` | as always |
| An expression | `0.045*f_GHz + 1.29` | in `freq` (Hz) or `f_GHz` (GHz), with the [expression language](expressions.html)'s functions: `log10`, `ceil`, `min`, `max`, `a < b ? x : y`; a unit glued to its number, `freq - 3GHz` |
| A table | `lna_nf.csv` | a CSV file of `freq,value` rows (the frequency in Hz, or with its unit, `26 GHz`; a first line that is not a row is its header), linear between rows |

Each is read at **its own stage's input frequency**: the chain's, and after a mixer the IF. A mixer's `Flo` is read
first, then the frequency moves, so `Flo = freq - 3GHz` keeps the IF at 3 GHz wherever the carrier is, and a filter's
`F1 = freq - 1.5GHz` follows the carrier. The budget, the levels and every bench read the resulting number, the one the
budget used, written in full: an ideal block on a schematic takes a number, not a function of the frequency.

A table is never extrapolated: a frequency outside its rows is a refusal that names the stage and the table's range. So
is an expression that cannot be evaluated there (`log10` of a negative number), or one that is not a real number. An
expression that reads any name but `freq` and `f_GHz` is not a number, and is refused as one. A relative table path
resolves from the workspace, as an SnP stage's file does.

The power-of-a-frequency forms a datasheet curve fit takes are one line each: a saturated power with a knee,
`freq < 30e9 ? 32.5 - 6*log10(f_GHz) : 45.79 - 15*log10(f_GHz)`; a loss rising with frequency, `0.012*f_GHz + 0.4`; an
amplifier stage's gain falling to 0 dB near the technology's limit, `33 - 15*log10(f_GHz)`.

## What the cascade computes {#cascade}

Each row of the cascade is the chain up to and including that stage, referred to the chain's input. With G the
available-power gains, T the noise temperatures and T0 = 290 K:

| Quantity | Formula |
|---|---|
| Gain | G = Π gᵢ |
| Noise (Friis) | Te = Σ Teᵢ / Gᵢ₋₁ with Teᵢ = T0·(Fᵢ − 1); F = 1 + Te/T0; NF = 10·log10 F |
| Each stage's share | (Teᵢ / Gᵢ₋₁) / Te: the same split a noise run's `NFpct` reports |
| Intercept | 1/IIP3 = Σ Gᵢ₋₁ / IIP3ᵢ (in mW); OIP3 = IIP3 + G |
| Compression | 1/IP1dB = Σ Gᵢ₋₁ / IP1dBᵢ; OP1dB = IP1dB + G − 1 dB |
| Output power | Pout = Pin + G |
| Thermal noise floor | 10·log10(k·Ts·B) + 30 dBm, the source's own noise |
| Output noise | 10·log10(k·(Ts + Te)·B·G) + 30 dBm |
| MDS | 10·log10(k·(Ts + Te)·B) + 30 dBm: the input signal at 0 dB SNR |
| Sensitivity | MDS + required SNR |
| SNR | Pin − MDS |
| SFDR | (2/3)·(IIP3 − MDS) |

The intercept sum takes every stage's third-order products in phase, the worst case. For ideal blocks with real gains
that is exactly what happens, and the two-tone simulation agrees to 2×10⁻⁴ dB. It takes each stage's products from the
wanted signal alone: an amplifier right after a mixer, with no filter between them, also sees the mixer's sum product,
and its third-order term mixes that with the IF onto the IF's own IM3, which the budget does not have (0.44 dB of IIP3 in
the dual-conversion example without its roofing filter). The compression sum is an approximation of
the same form: when two stages compress at similar levels the cascade compresses a little later than it predicts (see
[agreement](#agreement)).

**A mixer's noise figure is its single-sideband one**, and an SSB figure is stated with the image sideband terminated at
T0. The budget also takes the image as filtered of every earlier stage's noise, which is what a preselector is for. That
holds when the image filter is right in front of the mixer: a stage between them, an amplifier say, puts its own noise
at the image, which the budget does not have. **To see how much of an image a chain rejects**, give it a blocker at the
image's offset: the [levels](#levels) carry it through each filter's own response and each mixer, and its level at the
output, against the signal's, is the chain's rejection of that image.

## Transmit chains {#transmit}

**Direction ▸ Transmit** reads the same cascade from its other end. The input is the drive — the complex IF a DAC
delivers, say — and the chain ends at the antenna. Every stage, every row and every number of the cascade is the same
as a receive chain's; the totals add the transmitter's own, and leave out a receiver's sensitivity and range:

| Readout | Formula |
|---|---|
| Output power | Pout = Pin + G, the linear budget's |
| Output, CW | the carrier through every stage's compression in turn: a Rapp or Saleh stage its law's AM-AM, every other stage that compresses its tanh limiter's describing function, N(u) = (2/πu)·∫₀^π tanh(u·cos θ)·cos θ dθ at u = 2·√(P/IIP3) |
| Back-off from OP1dB | OP1dB − Pout, the cascade's OP1dB |
| Psat, and the back-off from it | the output's ceiling, P_k = min(P_(k−1) + g_k, Psat_k) through the stages, Psat_k a stage's own (a tanh stage's OIP3 − 10·log10(π²/4), 3.92 dB under it); Psat − Pout |
| ACLR | 2·(Pout − OIP3) − 10·log10 3, dBc: below |
| Noise floor | 10·log10(k·(Ts + Te)·G) + 30, dBm/Hz at the output |
| Noise in the receive band | the same sum with every stage's gain at the band's frequency, a filter passing \|S21\|² there and adding T0·(1 − \|S21\|² − \|S22\|²): dBm/Hz, and dBm in the band |
| Image, LO | an [IQ modulator's](#iqmod), each carried to the output through every stage at its own frequency, dBc |

**The ACLR relation, and what it assumes.** Take a memoryless nonlinearity to third order on the complex envelope,
y = a₁x + a₃x\|x\|², driven by a signal x that is complex Gaussian with power P and a flat spectrum of width B — OFDM
is close to one, by the central limit theorem. By Isserlis' theorem (L. Isserlis, "On a formula for the product-moment
coefficient of any order of a normal frequency distribution in any number of variables", Biometrika, 1918, and for a
complex Gaussian I. S. Reed, "On a moment theorem for complex Gaussian processes", IRE Transactions on Information Theory, 1962), E\|x\|⁴ = 2P² and E\|x\|⁶ = 6P³, so the
third-order output's part correlated with x is a gain change, 2a₃P·x, and the rest, of power 2\|a₃\|²P³, spreads as the
threefold convolution of the flat spectrum over 3B: two thirds of it in the channel and a sixth in each neighbour of
the same width, one bandwidth away. The two-tone intercept of the same nonlinearity is IIP3 = \|a₁/a₃\| per tone, so the
neighbour holds \|a₃\|²P³/3 against \|a₁\|²P in the channel:

```
ACLR = (P/IIP3)² / 3      →      ACLR = 2·(Pout − OIP3) − 4.77 dB
```

It assumes third order dominates, a back-off well under compression; a noise-like signal; the neighbour the signal's
width at one bandwidth's offset (an NR carrier is measured over its occupied bandwidth at the channel spacing, 98.28 MHz
at 100 MHz for a 100 MHz carrier, which moves the sixth to 0.158, 0.23 dB); and every stage's IM3 in phase, as the
cascade's intercept takes them. Two equal tones at the same total power put each IM3 product at 2·(Pout − 3.01 − OIP3),
1.25 dB under the relation's ACLR. The relation is an estimate: the [linear transmitter](#linear-tx) shows how far it
holds with a real PA law.

**The receive band.** A transmitter's own noise at a receiver's frequency — a duplex offset, a receiver beside it — is
what its band filter must remove. Give the band's offset from the carrier at the output, and its width: the noise there
is the Friis sum again with each stage's gain at that frequency. A filter's is its own response, and its noise is
Bosma's: it passes \|S21\|² of what arrives and adds T0·(1 − \|S21\|² − \|S22\|² ), its loss in its passband but almost
nothing in its stopband, where it reflects rather than dissipates; a reflected wave is the stage before's to absorb.

**A converter's sideband.** A Mixer goes on at its difference, \|f − `Flo`\|, by default; `Sideband` = Upper takes its
sum, f + `Flo`, as an up-converter does. The budget's numbers are the same either way, since an ideal multiplier puts the
same conversion gain into both; the frequency the chain goes on at, every stage after it reads, and the bench tracks,
moves. The bench's filter after a mixer is what removes the other sideband.

A transmit chain has no [link](#link) in front of it: a link's transmitter feeds a receive chain.

## The amplifier's laws {#laws}

An Amplifier's `Law` picks how it compresses, in the budget and in the simulation alike:

| `Law` | Gain against the input power s | IP1dB | Psat | IIP3 the budget uses |
|---|---|---|---|---|
| `Tanh` (the default) | the soft limiter on the instantaneous input | IIP3 − 8.9625 dB | OIP3 − 3.92 dB | `IP3`'s, its own third-order term |
| `Rapp` | (1 + (s/P0)^`P`)^(−1/(2`P`)), P0 = `Psat`/G | P0 + (10/`P`)·log10(10^(`P`/10) − 1) | `Psat`, its asymptote | the cubic's with its 1 dB point: IP1dB + 9.64 dB |
| `Saleh` | e^(jΦ(v)) / (1 + v), Φ(v) = a·v/(1 + b·v), v = s/s_sat, s_sat = 4·`Psat`/G | s_sat + 10·log10(10^(1/20) − 1), `Psat` − G − 3.12 dB | `Psat`, the AM-AM's peak, 6.02 dB into compression | its own third-order term, AM-PM in it: s_sat − 5·log10(1 + a²) |

`P` is Rapp's smoothness: at 1 the 1 dB point is 5.87 dB under the saturated input, at 2 1.16 dB, at 3 0.01 dB, a
hard clip's. Saleh's phase is stated by its value at the drive that saturates it, `PhiSat`, and its limit, `PhiMax`,
beyond it on the same side of zero: b = `PhiSat`/(`PhiMax` − `PhiSat`), a = `PhiMax`·b; both 0 is no AM-PM. The laws are
C. Rapp, "Effects of HPA-nonlinearity on 4-DPSK/OFDM-signal for a digital sound broadcasting system", Proceedings of
the 2nd European Conference on Satellite Communications, 1991, and A. A. M. Saleh, "Frequency-independent and
frequency-dependent nonlinear models of TWT amplifiers", IEEE Transactions on Communications, 1981, here with the
amplifier's own gain and a saturated power in place of their normalised constants.

**What Rapp's intercept is.** Rapp's law has no third-order term unless `P` is 1: at small drive its IM3 rises 2`P` + 1 dB
for every dB, and no intercept describes it. The budget gives it the cubic's with the same small-signal gain and the same
1 dB point — the textbook IP3 = P1dB + 9.64 dB — which is what an intercept sum and an ACLR estimate can take. At `P` = 1
its own third-order term is 0.76 dB lower. For `P` = 2 the cubic is pessimistic for two tones away from compression:
at the linear transmitter's operating point their IM3 reads 4.1 dB under it, and 20 dB further down the PA's products
fall at five times the drive. It is optimistic for a noise-like carrier at the same point: the NR carrier's peaks reach
past the 1 dB point, where Rapp's law clips harder than the cubic, and the PA alone reads its ACLR 0.8 dB worse than
the estimate.

**How the simulation does it.** A Rapp or Saleh law is a bandpass law, stated for a narrowband signal's envelope, so the
amplifier applies it to its input's analytic signal: the input voltage and its quadrature, which a linear companion
holds on a net of its own at the input's Hilbert transform (see
[System Components ▸ Amplifier](system-components.html#amp)). One tone's envelope is constant, so a one-tone run reads
the law at that drive exactly, and nothing at the harmonics; two tones beat, and their products are the law's own.
Every analysis carries it: single- and multi-tone harmonic balance, the envelope, and an S-parameter run, where the law
is its gain. A transmit chain's [PA bench](#tx-benches) reads its AM-AM and AM-PM over the drive against the closed
forms: 6.5×10⁻¹⁰ dB and 10⁻¹⁵°, and its 1 dB point to the three-point interpolation's 0.01 dB.

## The IQ modulator {#iqmod}

An `IQMod` stage is an image-reject up-converter of library blocks, placed as Transfer places every composed stage:

| Part | Placed as |
|---|---|
| The input's quadrature | a [90° Hybrid](system-components.html#hybrid90) (a Coupler of 3.01 dB at 90°), its fourth port terminated: the I arm and the Q arm |
| The LO | a P1Tone at `Flo`, 3.01 dB over `Plo` so each mixer is driven at its stated `Plo`, split by a 2-way [Combiner](system-components.html#combiner) whose second path is at +90° (the upper sideband) or −90°, plus `PhaseImb` |
| The mixers | two Mixers of `ConvGain`, `NF`, `IIP3` and `IsoLO_IF`, the Q one's gain `ConvGain` + `GainImb` |
| The sum | a 2-way Combiner |

The LO's split is a Combiner rather than a second hybrid because its path phase is flat and it stays passive at any
phase; a hybrid set off quadrature does not. With g = 10^(`GainImb`/20) and φ = `PhaseImb`, against one balanced
mixer the wanted sideband comes out at \|1 + g·e^(jφ)\|²/4 and its image at \|1 − g·e^(jφ)\|²/4:

| Quantity | Closed form |
|---|---|
| Gain | `ConvGain` + 20·log10(\|1 + g·e^(jφ)\|/2): `ConvGain` when balanced |
| Image rejection | \|1 + g·e^(jφ)\|² / \|1 − g·e^(jφ)\|²: 35.17 dB at 0.2 dB and 1.5°, which is what an uncalibrated modulator achieves |
| LO at its output | `Plo` − `IsoLO_IF` + 10·log10(1 ∓ sin φ), each mixer's feedthrough, the two in quadrature, summed at half each |
| IIP3, IP1dB | a mixer's, 3.01 dB up: each mixer takes half the input |
| NF | a mixer's single-sideband figure |

The image is at `Flo` ∓ f and the LO at `Flo`; the readouts carry each through the stages after the modulator at its own
frequency, so a band filter's rejection of them is in the numbers at the output. Its input is the chain's frequency, the
IF the modulator up-converts — high enough, for a wide carrier, that the LO and the image clear the adjacent channels:
the linear transmitter's 200 MHz puts them 200 and 400 MHz below a 100 MHz carrier.

## Digital and polar transmitters {#streams}

A transmitter's baseband is a stream of numbers until a converter makes it a voltage. A chain whose first stage is a
**DAC** or a **polar transmitter** starts from that stream: its input power is the stream's, and its benches drive it
with one (below). Either is a transmit chain's first stage, and only that.

**A DAC's floor is a noise temperature.** Each rail's quantisation error is Δ²/12, Δ = 2·`FS`/2^`Bits`, white over the
Nyquist band; referred to the input as a power into 50 Ω it is a noise of PSD Δ²/(12·50 Ω·`Fs`), a temperature
T_q = PSD/k at the DAC's input, which the cascade adds as it adds any stage's. Against the signal it is the textbook
relation (W. R. Bennett, "Spectra of quantized signals", Bell System Technical Journal, 1948; B. Widrow and I. Kollár,
*Quantization Noise*, Cambridge University Press, 2008, for when the uniform, white model holds):

```
SQNR  = 6.02·Bits + 1.76 − BO              over the Nyquist band, BO = 20·log10(FS / x_rms), x_rms = √(2·50 Ω·P)
SNR_B = SQNR + 10·log10(Fs / B)            in the bandwidth B: the processing gain
```

P is each rail's power, the chain's input power; x_rms the RMS envelope of the pair. The two are the same number: the
output's SNR at the DAC is SNR_B to 10⁻⁹ dB. A stream has no thermal noise of its own, so a DAC chain's source
temperature is 0 K. `PAPR` (budget only, 10 dB) is the signal's peak over x_rms: a back-off under it clips, which the
budget does not count and says so.

**The hold.** \|H(f)\| = d·\|sinc(d·f/`Fs`)\|, d = 1 for nrz and `Duty` for rz (A. V. Oppenheim and R. W. Schafer,
*Discrete-Time Signal Processing*, Prentice Hall, 1989). The stage's gain is `Gain` + 20·log10\|H(f)\| at the carrier's
offset f: the chain's frequency, which is 0 Hz for a zero-IF stream (a DAC chain may sit at 0 Hz). Across the band the
droop is EVM, since no equaliser takes it out: the spread of \|H\| over the band against its mean, (π²/(6·√180))·(B/`Fs`)²
for a centred band in nrz, 0.296 % at 19.08 MHz in 122.88 MHz. The first image of the band's edge f + B/2 lands
`Fs` − f − B/2 from 0 Hz, at \|H(`Fs` − f − B/2)\| / \|H(f + B/2)\| against the signal there: (f + B/2)/(`Fs` − f − B/2) in nrz.

**The rails.** Between the DAC and the IQ modulator each rail has its own copy of every stage (filters, amplifiers,
attenuators: nothing else), and the modulator takes them as its I and Q, with no hybrid. Each mixer sees its own rail
at the chain's power, and I·cos − Q·sin carries both rails' power, so the modulator's gain is 3.01 dB over one mixer's
conversion and its intercept the mixer's own. It takes the upper sideband: the lower would put the conjugate, I − jQ,
on the carrier.

**A polar transmitter** recombines the stream's amplitude a = \|x\| and phase φ in a supply-modulated amplifier. Its
output's amplitude is `Ks`·a, so P·`Ks`²: 20·log10 `Ks` of gain. With `Vsat` the law is `Ks`·`Vsat`·tanh(a/`Vsat`), averaged
over a Rayleigh envelope (an OFDM carrier's); its saturated output is (`Ks`·`Vsat`)²/(2·50 Ω).

**The mismatch.** The output is a(t − `dT`)·e^(jφ(t)), its error to first order −`dT`·ȧ·e^(jφ). The demodulator's delay fit
takes out its part along ẋ and leaves

```
EVM = dT·√(⟨ȧ²⟩·⟨a²φ̇²⟩ / ((⟨ȧ²⟩ + ⟨a²φ̇²⟩)·⟨a²⟩))  =  π·B·dT/√12   over the whole band
```

— the second form for a complex Gaussian signal with a flat spectrum of width B, whose envelope moves as fast in
amplitude as in phase, ⟨ȧ²⟩ = ⟨a²φ̇²⟩ = (π·B)²·⟨a²⟩/6. That is the trend F. H. Raab worked out for the Kahn transmitter
("Intermodulation distortion in Kahn-technique transmitters", IEEE Transactions on Microwave Theory and Techniques,
1996), distortion set by the delay times the bandwidth. The residual is wider than the signal: 0.436 of its power falls
in the channel, which the demodulator reads, and 0.215 in each adjacent channel of width B centred B away. Those two
shares are the model's own constants, from a Monte Carlo of it (±0.0007), so the budget's

```
EVM_dT  = √0.436 · π·B·dT/√12              1.14 % at 1 ns in 19.08 MHz
ACLR_dT = 0.215 · (π·B·dT)²/12             −42.0 dBc there
```

**The bandwidth.** The model: the envelope's AC part to its leading order — the \|ρ\|² term of a Rayleigh envelope's
autocorrelation, (π/16)·P spread as a triangle over ±B — through the aligned lowpass's error, 1 − H(f)·e^(j2πf·Tg),
spread as the signal is, so the channel takes 1 − \|f\|/B of it and the adjacent channel 1 − \|f − B\|/B. It leaves out
the envelope's spectral tail (an OFDM envelope has a cusp at every null), so it is optimistic once `Fam` is several
times B; at `Fam` = 2B it reads the adjacent channel as the Monte Carlo does and the EVM at about half.

**The readouts.** A chain from a stream adds three to a [transmit chain's](#transmit):

| Readout | Formula |
|---|---|
| EVM floor | √(10^(−SNR/10) + Σ e²), %: the output's SNR in the bandwidth, the DAC's floor in it; the DAC's droop, or the polar stage's mismatch and bandwidth terms. Not the PA's own in-band distortion, which by [the relation](#transmit) is 2·10^(ACLR/20) |
| ACLR | the estimate from the OIP3, plus the stream's own share of the adjacent channel: the DAC's floor held at B over the carrier, or the polar stage's terms, each carried by the stages after it at B above the carrier |
| DAC image | its first image at the output, carried through the stages after it at its own frequency, dBc |

The DataSet carries them as `EVMfloor_pct`, `ACLRdist_dBc` (the estimate alone) and `DacImage_dBc`, and the level
diagram's key gives the EVM floor and the ACLR; the DAC's floor is in its noise line.

## Arrays: N elements into a combiner {#array}

A **Combiner** stage makes the chain an array. Every stage before it is one element's branch, repeated N times; the
stages after it are the one receiver the elements are combined into. Every number is then referred to **one element's**
input, and the input power is per element.

- **Signal** from the N branches arrives in phase and adds in amplitude: the combiner's signal gain is
  +10·log10 N − `IL`.
- **Noise** from the N branches is uncorrelated and adds in power: the combiner's noise gain is −`IL` only. The array's
  noise temperature is therefore one branch's Friis sum, with the combiner's excess loss as a pad, and its NF is one
  element's.
- **SNR** rises by 10·log10 N, the **array gain**: SNR = Pin + 10·log10 N − 10·log10(k·(Ts + Te)·B) − 30. The MDS and
  sensitivity per element fall by the same amount.
- **Intercepts** after the combiner see N times one element's power, so the intercept and compression points referred
  to one element fall by 10·log10 N for the stages after it, and the output-referred ones rise.

`Steer` is the phase step of the arriving wave between adjacent elements. Transfer draws the wave's extra path to
element k as a line of (k − 1)·`Steer` in front of it, and sets element k's phase shifter to `Phase` + (N − k)·`Steer`,
which brings every element to the combiner in phase. Without a phase shifter before the combiner a non-zero `Steer`
misaligns the elements, and the budget says so: its coherent sum assumes they are aligned.

## The combiner's variants {#variants}

A Combiner stage is one of four, chosen by two parameters:

| | `Kind` = Passive | `Kind` = Active |
|---|---|---|
| **`Topology` = Direct** | one N:1 combiner of loss `IL` | the N:1 combiner, then an amplifier of `Gain`, `NF` and `IIP3` |
| **`Topology` = Tree** | log2 N levels of 2:1 combiners, each of loss `IL` | each level's 2:1 combiners each followed by an amplifier |
| **`Topology` = Levels** | one N:1 combiner, any N, with the loss of the ⌈log2 N⌉ levels a tree would take: ⌈log2 N⌉·`IL` | that combiner, then an amplifier |

`Levels` is the tree's budget for any number of elements, which a [receive array swept](#sweeps) over 6 or 11 elements
needs: an ideal N-way sum through ⌈log2 N⌉ lossy levels. It is transferred as one N:1 combiner of that loss, which its
benches check as they check a direct one. A real tree of 6 has unequal paths, which `PathIL` states.

| Parameter | Meaning |
|---|---|
| `N` | The number of elements. A tree combines in pairs, so its N is a power of 2; any other N is refused, with Direct as the remedy. |
| `Topology` | `Direct`, `Tree` or `Levels`. |
| `Kind` | `Passive` or `Active`. |
| `IL` | Each combiner's loss above the ideal split, dB. A tree takes one value for every level, or one per level, the first level (next to the elements) first: `0.3, 0.4`. |
| `PathIL` | Passive only: each element path's own extra loss, N values, dB — an amplitude taper, or an imbalance. Blank: none. |
| `Gain`, `NF`, `IIP3` | Active only: each combiner's amplifier; one value, or one per level. Its 1 dB compression point is its intercept's, IIP3 − 8.96 dB, as every amplifier's here. |
| `Steer` | The arriving wave's phase step between elements (see above). |

**Each variant's budget is the cascade of its own levels**, and every number is referred to one element's input:

- The element paths first. With amplitudes a_k = 10^(−`PathIL`_k/20), N in-phase inputs of power P combine to
  (Σa_k)²·P/N and their uncorrelated noise to (Σa_k²/N)·n²; the weighting itself is a matched passive network at T0,
  adding T0·(N/Σa_k² − 1) of noise. With equal paths all three are N·P, n² and nothing.
- Then each level: a passive n-way level of loss L gains n·(1/L) in signal and 1/L in noise and adds T0·(L − 1) of
  noise; an active level's amplifier follows it with its own gain, noise figure and intercept.
- Signal and noise gains, noise temperatures and intercepts cascade through the levels exactly as they do through a
  chain: Friis with the noise gains, the intercept sum with the signal gains. The amplifiers of one level are in
  parallel, each fed by its own pair, and identical, so together they behave as one amplifier seeing the combined
  power.
- The **array gain** is the signal gain over the noise gain: (Σa_k)²/Σa_k², which is N for equal paths and less with a
  taper.

So a passive tree's loss accumulates level by level, and its noise figure is its whole loss; a direct combiner has one
loss. An active combiner's noise figure is set by its first level, and its intercept by its last, which sees the most
power: in the example's array, the active tree's IIP3 is 7 dB below the direct active combiner's.

The example's array, the same four elements into each variant (`IL` 0.5 dB, the amplifiers 10 dB, NF 2 dB,
IIP3 10 dBm), at the combiner's output and per element:

| Variant | Gain | NF | IIP3 | OIP3 | Array gain |
|---|---|---|---|---|---|
| Direct, passive | 18.52 dB | 1.33 dB | −9.19 dBm | 9.33 dBm | 6.02 dB |
| Tree, passive | 18.02 dB | 1.35 dB | −9.19 dBm | 8.83 dBm | 6.02 dB |
| Direct, active | 28.52 dB | 1.43 dB | −11.88 dBm | 16.64 dBm | 6.02 dB |
| Tree, active | 38.02 dB | 1.45 dB | −18.77 dBm | 19.26 dBm | 6.02 dB |

## A link in front of the chain {#link}

On the **Link** tab, **a transmitter and a path in front of the chain** adds a transmit array and a line-of-sight path:
the power they deliver to each receive element becomes the chain's input power, and the receive array is the chain's
own [Combiner](#array) (one element with none). The transmitter and the path are not stages, because a stage before the
combiner is one receive element's branch and every number is referred to that element's input. Each input but
`Elements` and `Distance` may be a number, an expression or a table, [read at the carrier](#scaling).

| Transmitter | Meaning |
|---|---|
| `Psat` | Each transmit element's PA saturated output, dBm. |
| `Backoff` | Its output back-off: Pout = `Psat` − `Backoff`. The [TX bench](#tx) shows what a back-off costs in EVM and ACPR. |
| `Loss` | From each PA to its antenna element (a switch, a line), dB. |
| `AntennaGain` | Each transmit element's gain, dBi. |
| `Elements` | Mtx, the transmit elements, all in phase toward the receiver. |
| `PaGain`, `PAE`, `Pdc` | For the DC power: the PA's gain and its power-added efficiency at the back-off, %, or each element's DC power stated. |

| Path | Meaning |
|---|---|
| `Distance` | The path: metres, or with `m` or `km` (`200 m`, `0.2 km`). |
| `Attenuation` | Extra or atmospheric attenuation along it, dB/km: a number, an expression or a table of the carrier. |
| `RxAntennaGain` | Each receive element's gain, dBi. |

```
Pout = Psat − Backoff
EIRP = Pout − Loss + AntennaGain + 20·log10(Mtx)           Mtx PAs in phase: Mtx of power, Mtx of directivity
Lfs  = 20·log10(4π·d·f / c)                                 free space
A    = Attenuation · d / 1000
Prx  = EIRP − Lfs − A + RxAntennaGain                       at each receive element
Pin  = min(Prx, IP1dB − RxBackoff)                          the chain's input
```

**The receiver's ceiling.** A link that delivers more than the receiver takes is held at its input P1dB less
`RxBackoff` (10 dB), as power control or a variable gain would hold it, and the Notes say so. Without a link the input
is not moved, and the Notes say when it is above that ceiling. Which stage sets the compression is in the cascade
table's **P1dB share** column, each stage's share of 1/IP1dB; when the largest share is after the combiner the Notes
name it. That is the common case in an array: the elements' signals reach a mixer after the combiner in phase, N times
one element's power, so a mixer compresses long before any element's LNA does.

**The combiner convention.** A Combiner here is a matched N:1 network: N in-phase element signals come out at N times
one element's power, their noise once. Some analyses count the in-phase sum in voltage instead, 20·log10 N of signal
and 10·log10 N of noise. The SNR, and every readout below, is the same either way; the absolute level after the
combiner differs by 10·log10 N.

EIRP and the path have no circuit to simulate; the benches run the receiver at the power the link delivers, and the
closed forms above are checked against an independent calculation, `testdata/chain/link_reference.py`.

## SNR, rate, DC power and the NF allowance {#readouts}

Every chain, with a link or without, reads as a receiver of data. The **Receiver** row of the Link tab holds the inputs:

| Input | Meaning |
|---|---|
| `FBW` | A fractional bandwidth: B = `FBW` · frequency (0.1 for 10 %), which then replaces the Bandwidth input, for the noise too. Blank: the Bandwidth input. |
| `Streams` | Orthogonal streams (two polarisations, MIMO layers), each with its own hardware: the rate and the DC power both multiply by it, and the energy per bit does not move. |
| `NfAllowance` | The NF allowance's step, dB (1). |
| `RxBackoff` | The receiver's ceiling below its input P1dB, dB (10). |

| Readout | Formula |
|---|---|
| Noise at the input | the MDS, 10·log10(k·(Ts + Te)·B) + 30 − 10·log10(A): kTB and the array's NF |
| NF of the array | 10·log10(F/A), F the per-element noise factor and A the array gain; in linear terms F/A = F_A/M + (F_B − 1)/G_A for equal paths and F_A/A + (F_B − 1)/G_A with a `PathIL` taper: F_A the front end's to the combiner's output, F_B the stages after it, G_A the gain to that output with the array gain; below 0 dB for a large array |
| SNR, margin | Pin − MDS; SNR − required SNR |
| Rate | S · B · log2(1 + 10^(SNR/10)), Shannon's capacity over S streams |
| PA DC power | (Pout − Pout/G) / PAE, at the back-off, unless the transmitter's `Pdc` is stated |
| DC power | S · (Mtx · P_PA + Mrx · Σ P_branch + Σ P_after): the branch's stages once per receive element, the combiner and every stage after it once |
| Energy per bit | DC power / rate |
| NF allowed after a stage | 10·log10(1 + F_k·(10^(X/10) − 1)·Gn_k), X the allowance: the largest NF the rest of the chain may have for the whole to stay within X dB of the NF up to stage k (F_k and Gn_k its cumulative noise factor and noise gain) |

The **NF left** column of the cascade table is the last of these: after the example's LNA, the rest of the chain may
have an NF of 16.7 dB and cost the chain 1 dB. It is what tells how much gain an LNA needs before a noisy mixer.

## Sweeps {#sweeps}

The **Sweeps** row of the Link tab takes up to three sweeps, each one line, as the Blockers are:

| Sweep | Line | Points |
|---|---|---|
| `Frequency` | `20 GHz, 40 GHz, 41` | carriers evenly from the first to the last |
| `TxElements` | `4, 1024, 9` | transmit array sizes spread evenly in ratio, each rounded to a whole element and kept once |
| `RxElements` | `1, 64, 13` | the receive Combiner's N, the same way; 1 is one element with no combiner |

Each point is the whole chain budgeted again there, every expression and table read at that point's carrier. Each sweep
has its own tab: the link levels (EIRP, the received power at each element, the chain's input, the ceiling and the
noise, the MDS), the SNR and margin, the rate and the DC power, over its own axis, a log one for an array's sizes. A point the budget refuses (a table read
outside its range) is left out, and one note gives how many and why; a carrier where a Filter's passband no longer holds
the stage's frequency is noted, since the budget takes its loss there and not its roll-off.

{{ui: chain-budget-link}}

A sweep is not a bench: transfer the chain at a point (set the frequency, or `Elements`, or `N`) and its benches check
that point. `circuitrf chain -o` writes each sweep as a group of the DataSet, named after it (`Frequency.SNR_dB`).

## Transfer to Schematic {#transfer}

**Transfer to Schematic** saves the chain and writes its test-bench cells beside the `.cchain`, named after it, each
with a Data Display of the same name that Simulate opens on the bench's own result. Every chain gets 1-Tone, 2-Tone,
Envelope and Levels; a chain with blockers a Blockers bench; a chain with a canceller a Canceller bench; an array whose
branch has no mixer Steering (when the branch has a phase shifter) and Combiners; and, when asked, the Demo. A chain
with more than one mixer gets most of them once per [conversion](#conversions):

| Bench | Source | Analyses | Measures |
|---|---|---|---|
| `<chain> 1-Tone` | a P1Tone per element (port 1 … N), a Port at the output | without a mixer, an [S-parameter run](simulations.html#sp-noise) with `Noise=yes`; with one, [harmonic-balance noise](simulations.html#hb-noise) with the LO running. And a harmonic-balance drive sweep at the predicted IP1dB | gain, NF, each stage's cumulative NF from the run's own shares, IP1dB, OP1dB |
| `<chain> 2-Tone` | a PnTone per element, 1 MHz apart, 50 dB below the predicted IIP3 | [multi-tone harmonic balance](simulations.html#two-tone) | gain, IIP3, OIP3, and each stage's gain and OIP3 at its output |
| `<chain> Envelope` | a MOD_Source per element: an NR carrier, its source noisy at the source temperature | [envelope](simulations.html#envelope-noise) with `Noise=yes` and `EvmSplit=yes` | in-channel SNR, the EVM's noise part, output power and output noise |
| `<chain> Steering` | a plane wave: one Port into a Combiner used as a splitter, path k delayed by the wave's extra path; the elements and the combiner, a Port at its output | an S-parameter run with `Noise=yes` in a sweep of `theta_in` (−90° to 90°) within a sweep of `theta_steer` (−30°, 0°, 30°) | the pattern against the arriving angle beside the closed-form array factor; at each beam's peak the gain, the NF and the array gain. See [Steering](#steering) |
| `<chain> Combiners` | the same elements into each of the four [variants](#variants), each fed by its own wave at broadside | one S-parameter run with `Noise=yes` | each variant's gain and NF, beside its budget, intercepts and array gain |
| `<chain> Levels` | a PnTone per element, two tones 1 MHz apart, each at the input power | multi-tone harmonic balance, on the 2-Tone bench's mixing order | the signal and its IM3 at every node. See [Levels](#levels) |
| `<chain> Blockers` | a PnTone per element with every blocker, an isolator after every stage | harmonic balance | every blocker at every node |
| `<chain> Canceller` | the canceller on its own: a wanted signal and an interference from MOD_Sources | envelope runs, swept | the depth against each error, the wanted signal's EVM, SNR and constellation with and without cancellation. See [The canceller](#canceller) |
| `<chain> Demo` | a MOD_Source of NR 64-QAM and one of DBPSK per element; the LO with a phase-noise mask | envelope with `Noise=yes` and `EvmSplit=yes`, the input power swept | the constellation, the EVM against the input power, its noise and distortion parts. See [The demo](#demo) |
| `<chain> TX` | a chain with a [link](#link): one transmit element's PA, an NR 64-QAM carrier from a MOD_Source, through its `Loss` | envelope, swept over the back-off from `Psat` | the output against the budget's Pout − Loss, the EVM and the adjacent-channel ratios. See [The TX bench](#tx) |

The chain's parts are placed as written. Three things are the bench's and not the chain's:

- the **sources, the load and each mixer's LO**, a P1Tone at `Flo` and `Plo`;
- an **isolator in front of each mixer** (a Circulator whose third port is terminated at the ambient). It terminates the
  mixer's image at T0, the termination its SSB noise figure is stated against. A filter in front of a mixer reflects the
  image instead, and the mixer's noise then differs: in the superhet example the harmonic-balance noise figure reads
  0.28 dB higher without the isolator. Delete it to see what your own image termination does;
- in an array, the **arriving wave's path**: a TLIN of (k − 1)·`Steer` in front of element k (on the Steering and
  Combiners benches, the splitter's own path phases instead).

Every bench sets `temp = 16.85`, which puts every part at 290 K, the temperature a noise figure is stated at. Each row
of its MEAS block reads one analysis, so `circuitrf hb … -a SW1` evaluates the compression rows on its own. The envelope
SNR is measured over the NR carrier's occupied bandwidth (the 15 kHz-spacing channel nearest the chain's bandwidth), and
its prediction beside it is computed in that bandwidth.

An array's 1-Tone bench has N + 1 ports. Its gain is |Σₖ S(N+1, k)|², which is the budget's only if every phase shifter
removes its element's steering, and its NF is one element's: F = 1 + (F₁ − 1)·|S(N+1, 1)|²/Σₖ|S(N+1, k)|², where F₁ is
the noise factor from port 1 with the other elements' terminations noiseless — F = 1 + (F₁ − 1)/N for equal paths.

### A transmit chain's benches {#tx-benches}

A [transmit chain](#transmit) is driven at its input and read at its output, so its benches are its own:

| Bench | Source | Analyses | Measures |
|---|---|---|---|
| `<chain> 1-Tone` | a P1Tone at the input (port 1), a Port at the output | harmonic balance swept over the drive from 20 dB under the input power to 10 dB past the back-off from Psat, with the predicted IP1dB ± 0.25 dB in it; harmonic-balance noise at the input power, 1 MHz off the carrier and in the receive band | the output against the drive beside the budget's CW output; the 1 dB point; the noise floor and the receive band's; an IQ modulator's image and LO at its own output and at the chain's |
| `<chain> 2-Tone` | a PnTone of two tones 1 MHz apart, their total the input power | multi-tone harmonic balance at three levels: 20 dB and 6 dB under, and at, the input power | the IM3 against the level beside the intercept's 2·(P + G − OIP3); the intercept 20 dB down |
| `<chain> Envelope` | a MOD_Source at the input: an NR 64-QAM carrier, noiseless | envelope at the input power and 3 dB either side | the output power beside the linear budget, the ACLR beside the budget's estimate, the EVM, the spectrum |
| `<chain> PA` | the PA stage alone, from a MOD_Source at its own input and frequency | harmonic balance swept over its drive (a MOD_Source is a CW carrier there), and an envelope run at its input power | AM-AM and AM-PM beside its law's closed forms, its 1 dB point and saturation, its efficiency; the NR carrier's ACLR and EVM through it |

A chain from a [stream](#streams) has its Envelope bench and its PA's only: harmonic balance has no stream, and a DAC
is refused there by name. Its Envelope bench drives the stream itself, at baseband about the carrier, and places the
first stage as its System blocks:

| First stage | Placed as |
|---|---|
| DAC | two MOD_Sources, `Baseband=i` and `q`, each into its own DAC and its own copy of every stage up to the IQ modulator, whose mixers take the two rails, the LO the carrier |
| Polar transmitter | a MOD_Source pair, `Polar=amp` and `phase`: the amplitude through a Delay of `dT` and the Butterworth lowpass into a Supply-Modulated Amplifier's supply; a P1Tone at the carrier, limited, turned by the phase half in a Phase Modulator (`Kp` 1 rad/V), delayed by the lowpass's DC group delay (and by \|`dT`\| when the phase is the later) |

It runs at the standard step (a fraction of a step is interpolated) over the drive, and adds the budget's EVM floor to
the display, with the ACLR beside the estimate that includes the stream's share.

The PA is the chain's last Amplifier with a Rapp or Saleh law, else its last that compresses. An ideal amplifier draws
its stated `Pdc` at every drive, so its efficiency is the power it adds over that: class A's shape, 50 % at saturation
for a `Pdc` of twice `Psat`. The NR carrier is the 15 kHz-spacing channel nearest the chain's bandwidth up to 50 MHz, as
a receive chain's, and the 30 kHz one above it (up to 100 MHz, 273 resource blocks).

The 1-Tone bench puts an isolator after an IQ modulator, the bench's and not the chain's: the filter after it reflects
the image and the LO, and a node in front of a reflection reads the incident wave plus the reflected one; behind the
isolator the modulator's output reads the wave going forward, as the Blockers bench's nodes do. Every bench is checked
before it is written, as a receive chain's are: an IQ modulator's LO is a tone, so the 1-Tone run has two tones, the
2-Tone run three, each at the order its numbers converge at or the highest under the 600-product cap; the envelope run
has two fundamentals, the carrier's and the LO's, the LO more than three bandwidths from it.

### The TX bench {#tx}

The TX bench is one transmit element: an NR 64-QAM carrier into an Amplifier of `PaGain` whose output intercept is
`Psat` + 10·log10(π²/4) = `Psat` + 3.92 dB — the tanh limiter saturates its fundamental that far below its intercept —
then the transmitter's `Loss` as an Attenuator, into 50 Ω. An envelope run is swept over the back-off `BO` from 10 dB of
overdrive to 16 dB back, and the link's own, with the drive `Pin = Psat − BO − PaGain`. Its display: the output at the
antenna against the budget's `Psat − BO − Loss`, their difference, the EVM and the two adjacent-channel ratios against
the back-off, and a table at the link's back-off.

The budget takes the PA's output as `Psat − Backoff`; the bench shows what that leaves out. The ideal PA is soft, its
1 dB compression point 6 dB below `Psat`, and an NR carrier's peaks reach it: in the example at 8 dB of back-off the
output is 0.92 dB below `Psat` − 8 dB and the EVM 6.3 %, at 12 dB 0.41 dB and 2.9 %. Choose the back-off for the EVM and
ACPR the link needs, from this table. A memoryless PA's EVM and ACPR do not depend on the bandwidth, so the bench's NR
carrier is the 15 kHz-spacing channel nearest the chain's bandwidth, 50 MHz at most; the subcarrier spacing changes
neither the budget nor what a memoryless block does to a carrier's EVM.

## More than one conversion {#conversions}

Every bench Transfer writes is checked against what its runs take: a harmonic-balance run at most six tones and 600
mixing products, its noise run one or two tones, and an envelope run two fundamentals, the carrier's and one LO's, with
the LO more than three of the carrier's bandwidths from it. Each LO is a tone of its own, so a chain with two mixers
outgrows them: its 1-Tone bench's noise run would have three tones, and its envelope runs three fundamentals.

Such a chain is checked **once per conversion**. Conversion k runs from the stage after mixer k − 1 (from the input, for
the first) to mixer k (to the output, for the last), so each holds one mixer and the filtering in front of it. Its
benches are named after the IF it ends at, `<chain> IF1 1-Tone`, `<chain> IF2 1-Tone` and so on, and each is the chain
from that point on:

- driven at the IF the conversion before it ends at, at the budget's level there, with every blocker as it arrives
  there;
- from a source whose temperature is the noise the stages before it deliver, (Ts + Te)·Gn, so that its SNR and its
  noise floor at every node are the whole chain's;
- beside its own budget: its gain, NF and intercepts are those of its own stages, and the whole chain's follow from them
  by the same sums, F = F₁ + (F₂ − 1)/G₁ and 1/IIP3 = 1/IIP3₁ + G₁/IIP3₂.

A bench one run can hold is still written for the whole chain, at a lower mixing order when the cap needs it. A
dual-conversion chain's 2-Tone bench is one: four tones (the two, and both LOs) at MaxMixOrder 5, the least that carries
the output's IM3, retain 341 mixing products, where the 7 its IF stages converge at would retain 1,121. Transfer says
so, and the intercepts read 3×10⁻³ dB high ([agreement](#agreement)). A run of the whole chain also sees what a
conversion's bench, fed a clean IF, cannot: the cross-products of the [cascade](#cascade)'s note. The 1-Tone, Levels and Blockers benches go per
conversion together, since the Levels display reads all three node by node. In the window the Levels tab then shows
the budget's lines, and each conversion's display its simulated points.

A bench no run can hold even per conversion is not written, and both Transfer and `circuitrf check` say why (in `check`,
a `chain.bench.left-out` warning). Two causes besides too many tones or products:

- **An LO within three bandwidths of the envelope's carrier.** The Envelope and Demo benches send an NR carrier near the
  chain's bandwidth, 5 MHz for 4.5 MHz. A second IF of 10.7 MHz puts the second LO 10.7 MHz from that carrier at the first
  IF, and the second conversion's Envelope and Demo benches are left out; a second IF of 21.4 MHz runs.
- **Two products on one frequency, one of them read.** A blocker exactly on a mixer's image converts to exactly where
  another tone's product lands, and a harmonic-balance run cannot tell the two apart. Move the blocker a little off the
  image, as the example's second blocker is. Two products the bench reads neither of do not stop it: blockers spaced
  evenly put their differences on one frequency, and the bench runs, a few thousandths of a dB off, and says so. An
  envelope run takes no coincidence at all: an LO at two thirds of the signal puts the IF on a second product too.

## Steering the beam in simulation {#steering}

The Steering bench is the array at the combiner, fed by a plane wave and steered from its own VAR block:

| Variable | Meaning |
|---|---|
| `theta_in` | The arriving wave's angle from broadside, degrees. The bench sweeps it from −90° to 90° in 1° steps. |
| `theta_steer` | The angle the phase shifters steer to, degrees. The bench sweeps −30°, 0° and 30°. |
| `d_over_lambda` | The element spacing in wavelengths, 0.5. |
| `taper` | An amplitude taper, dB: the two edge elements' paths lose `taper` more, the others a share in proportion to their distance from the centre. 0. |
| `psi_in`, `dphi` | 360°·d/λ·sin θ for the wave and for the steer: the phase step between adjacent elements. |

The wave is one Port into a Combiner used as a splitter, whose path k has the phase −(k − 1)·`psi_in`: element k
receives the wave (k − 1) element spacings later. Each element's phase shifter advances it by (k − 1)·`dphi` (its line is
(k − 1)·(360° − `dphi`) long, the same phase at the bench's frequency and never a negative length), and the taper sits on
the combiner's paths. Because the splitter shares the wave's power equally and its own isolation network is noisy at
T0, each element sees an equal share of the signal and its own, uncorrelated, thermal noise, as an element of a real
array does from its own antenna.

Its display plots the **pattern**, the simulated gain over the budget's peak gain at the combiner, against `theta_in`
for each steer angle, and beside it the closed-form array factor:

```
AF(θ_in) = | Σₖ a_k · e^(−j·(k − 1)·(ψ_in − ψ_steer)) | / Σₖ a_k,      ψ = 2π·(d/λ)·sin θ
```

with a_k each path's amplitude weight (its `PathIL` and the `taper`). Each peak sits at its steer angle, and there the
table reads the gain, the NF from the wave's port and the **array gain**, 10·log10 N less what the NF says it costs over
one element's own: 6.02 dB for four equal elements, at every steer angle, with the NF unmoved. With a taper the main
lobe widens and the sidelobes fall, and both the pattern and the closed form lose the taper's coherent loss at the
peak. Change `d_over_lambda` to 1 and the grating lobes appear at ±90°.

Edit a sweep's values, or delete the sweeps and set `theta_in` and `theta_steer` by hand: everything is an ordinary
variable.

## Levels at every node {#levels}

The **Levels** tab and the Levels display follow the chain node by node: node 0 is the input and node k the output of
stage k. At every node:

| Quantity | Budget |
|---|---|
| Signal | Pin + G, per tone of two at the input power |
| Noise floor | 10·log10(k·(Ts + Te)·B·Gn) + 30 dBm, the noise in the bandwidth |
| NF | the cumulative noise figure |
| IM3 | 3·P − 2·OIP3, each third-order product of the two tones, absolute |
| Each blocker | its input level plus every stage's gain at its own frequency: a filter's own response there (the model the Filter block simulates with, its IL included), an S-parameter data stage's file there, a mixer moving it to \|f − Flo\| |
| A canceller's interference | from the canceller's input, where it is stated, and its residual after it |
| Margin to compression | OP1dB − the larger of the signal and the blockers; before anything compresses, the whole chain's IP1dB less the input's |
| SNR | the signal over the noise floor |

The simulated points come from three benches: the **Levels** bench (two tones each at the input power: the signal and
the IM3), the **Blockers** bench (the blockers alone, an isolator after every stage so that each node's voltage is the
wave going forward, not a reflection standing on a filter's stopband) and the **1-Tone** bench (each stage's
cumulative NF, from the noise run's own shares, and the noise floor that NF gives at the node through the budget's
noise gain). In the window they appear once those benches have been simulated beside the `.cchain`; in the display
they are its points, the budget its lines. The displays plot against a variable `nodeix` that each bench sweeps
around a trivial DC solve, which is how one number per node becomes one curve.

The dBm plots, in the window and in the display, reach 10 dB over the highest level they draw and 20 dB under the lowest
level but the IM3, each rounded out to 10 dB, and half a node beyond the input and the output. Two tones far under the
noise floor would otherwise take most of the height, so an IM3 under the frame is clipped there and its legend entry
says so: **IM3 (clipped)**. The dB plot reaches 10 dB beyond its NF, SNR and margin. A level that does not exist is left
out, never drawn at a stand-in: the IM3 before anything is nonlinear, the interference before the canceller, and a
blocker from the stage on that passes nothing of it (a band-pass filter at 0 Hz, where a mixer can put one).

### The level diagram {#level-diagram}

Tick **Levels** in the Blocks view and the chain's level plan is drawn under its blocks, each node on the gap between
two blocks: the input on the arrow before the first, each stage's output on the arrow after it.

{{ui: chain-level-diagram}}

At every node, in dBm:

| Line | What it is |
|---|---|
| signal | Pin + G, its value written at each node |
| noise floor | the noise in the bandwidth, kT·B with the cumulative NF and the gain, its value written at each node |
| sensitivity | MDS + the required SNR, carried to the node by the gain: the signal the chain needs |
| P1dB | the OP1dB of the stages up to the node, from the first stage that compresses, its value written at each node |
| IM3 | each third-order product of two tones at the input power, from the first stage that has an intercept |
| each blocker, the interference | as in the table above |

Shaded between them: the **SNR**, from the noise floor up to the signal; the **headroom**, from the signal up to the
P1dB; and, hatched, the **SFDR**, from the noise floor up to where the IM3 would reach it, (2/3)·(OIP3 − noise). The key
under the plot gives the SNR and the SFDR at the output, and the **margin to P1dB** there as the Levels tab gives it: from
the strongest carrier, which it names (the signal, or a blocker). Each line is named at the output with its last value.
The figure is framed as the Levels tab is, and its window also holds the P1dB, the sensitivity and a link's EIRP; an
IM3 under it is a ▼ on the bottom edge at its node, and its name at the output keeps its value. Over the plot, the
frequency of each run of nodes, a dashed line at each mixer; in an array, the nodes that carry one element's levels are
marked **each of N elements**: the branch the blocks draw once, "× N", up to the combiner.

A [transmit chain](#transmit)'s diagram is read toward its output. The signal is **Pout**, the output power at every
node, and over the P1dB, dashed, is the **Psat** referred to each node: the ceiling the stages up to it give,
P_k = min(P_(k−1) + g_k, Psat_k), the chain's Psat at the output. The shading under the P1dB is the back-off. The key
gives the **back-off** at the output from both, the **noise floor** in dBm/Hz, and the **ACLR** estimated from the
OIP3 (an estimate, as [the relation](#transmit) says). There is no sensitivity and no SFDR, which are a receiver's. A
chain from a [stream](#streams) keys its ACLR with the stream's share and its EVM floor; a DAC's quantisation floor is
its noise line, from the DAC's node on.

With a [link](#link), its front stands left of the input: the EIRP, the path down to the power each element receives,
the path's loss (and the receive antenna's gain) written on it, and the receiver's **ceiling**, IP1dB less
`RxBackoffDb`, marked at the input. When the power arriving is above the ceiling it is drawn hollow, held down to it.

Click a node to select the stage whose output it is; the selected stage's node is drawn selected. **Copy** puts the
blocks and the diagram on the clipboard together. `circuitrf chain <file> --level-diagram levels.png` (or `.svg`) draws
the same diagram on its own, the nodes evenly spaced.

## The canceller {#canceller}

A **Canceller** stage subtracts a weighted copy of an interference from the main path: a reference path from the
interference's own source, through an amplitude setting, a phase setting and a delay, into a directional coupler's
isolated port, the main path being the coupler's through arm.

| Parameter | Meaning |
|---|---|
| `Coupling` | The coupler's coupling, dB. The main path loses 10·log10(1 − c) of it. |
| `IL` | The coupler's excess loss, dB. |
| `Pint` | The interference at the canceller's main input, dBm. |
| `Bint` | The interference's bandwidth. |
| `Pref` | The reference input: the interference at its source, dBm. The path's gain follows from it. |
| `NFref` | The reference path's noise figure, dB. Blank: passive, at T0. |
| `AmpErr`, `PhaseErr`, `DelayErr` | The cancellation path's amplitude error (dB), phase error (degrees, an advance) and delay mismatch (the reference path later, seconds: `0.1 ns`). |

**On the wanted signal** it is the through arm, a loss L_t = 10^(`IL`/10)/(1 − c), and the noise the reference path
injects. That path needs a gain G_ref with G_ref·P_ref·c = P_int·(1 − c); its output is at T0·F_ref·G_ref, coupled by
c/(1 − c) against the through arm, so

```
F = L_t + (F_ref·G_ref − 1)·c/(1 − c)
```

A passive path (F_ref·G_ref = 1) adds nothing to the coupler's loss.

**On the interference** the residual is the fraction R the subtraction leaves. With an amplitude ratio
a = 10^(`AmpErr`/20), a phase error φ and a delay mismatch τ, a carrier of bandwidth B and flat spectrum, centred ν from
where the phase was set, leaves the band's average of |1 − a·e^(j(φ − 2πfτ))|²:

```
R = 1 + a² − 2a·cos(φ − 2πντ)·sinc(πBτ),      sinc x = sin x / x,      depth = −10·log10 R
```

33.5 dB at 0.1 dB and 1°; with 1 ns of delay mismatch alone, 40.8 dB over 5 MHz, 28.8 dB over 20 MHz and 22.8 dB over
40 MHz. The window's **Canceller** tab draws the depth against the phase error for five amplitude errors, and against the
bandwidth for four delay mismatches. The levels carry the interference and its residual node by node.

**The Canceller bench** studies it with real signals. Two MOD_Sources, which the [Signal
Designer](signal-designer.html) edits: the wanted signal, an NR 64-QAM carrier and a QPSK one, and the interference,
three NR carriers of 10, 5 and 20 MHz, the first two under the wanted ones. The interference is split: one share leaks
into the main path through a line of 1 ns, the other is the reference path — an Amp for the amplitude, one path of a
2-way Combiner for a flat phase, a line for the delay — into the coupler's isolated port. Its VAR block sets the errors,
`amp_err`, `phase_err` and `delay_err`, and two switches, `canc_on` (the reference path) and `sig_on` (the wanted
signal). Its envelope runs:

- the interference alone, uncancelled: the reference each depth is measured from;
- the depth against each error, swept, the others at their nominal values, at every interference carrier: beside it the
  closed form above at that carrier's bandwidth and offset, so the 20 MHz carrier shows how a delay mismatch costs a
  wider signal more;
- the wanted carriers with and without cancellation, their noise on: EVM, SNR, and each constellation both ways.

Open either source in the Signal Designer to change its modulation (LTE, NR, DBPSK, FSK, CSS and the rest it offers) or
its bandwidth; the bench measures whatever it sends.

## The modulated-signal demo {#demo}

**include a modulated-signal demo**, in Transfer to Schematic's drop-down (or `--demo`, or `"Demo": true` in the
`.cchain`), adds `<chain> Demo`: the chain
demodulated end to end. Each element is fed an NR 64-QAM carrier and, from a second source beside it, a DBPSK carrier
at twice the bandwidth's offset, joined by a 2-way Combiner, each at the input power and noisy at the source
temperature. Each mixer's LO carries a phase-noise mask, which an envelope run plays as a random phase. An envelope run
with `Noise=yes` and `EvmSplit=yes` is swept over the input power in seven steps, from 5 dB of SNR to 5 dB past the
input P1dB. Its display:

- the 64-QAM constellation at the output, received points over the ideal ones, at the middle and the highest power;
- the EVM against the input power, 64-QAM and DBPSK, and beside it the budget's from its SNR, EVM ≈ 1/√SNR, which holds
  where noise dominates;
- the EVM's split into its noise and distortion parts: noise at low power, the LO's phase noise as a floor, distortion
  past compression;
- the SNR in the carrier's channel, simulated and the budget's.

## A linear transmitter {#linear-tx}

The example's `Linear TX.cchain` is a 3.5 GHz transmitter for a 100 MHz NR carrier at 30 kHz subcarrier spacing, from a
200 MHz IF to the antenna:

| Stage | Part | Gain | NF | Compression |
|---|---|---|---|---|
| IQ | `IQMod`, LO 3.3 GHz at 7 dBm, upper sideband, 0.2 dB and 1.5° of imbalance, `IsoLO_IF` 50 dB | −5.90 dB | 12 dB | IIP3 18.01 dBm (each mixer 15 dBm) |
| BPF1 | `Filter`, 5th-order Butterworth, 3.4 to 3.6 GHz | −1.5 dB | 1.5 dB | |
| DRV | `Amp` | 20 dB | 4 dB | OIP3 32 dBm (tanh) |
| PA | `Amp`, `Law` = Rapp, `Psat` 37 dBm, `P` 2, `Pdc` 10 W | 30 dB | 6 dB | IP1dB 5.84 dBm, cubic IIP3 15.47 dBm |
| BPF2 | `Filter`, 5th-order 0.1 dB Chebyshev, 3.4 to 3.6 GHz | −0.6 dB | 0.6 dB | |

Driven at −14 dBm, its budget, every number of which an independent calculation reproduces to 10⁻⁶ dB from the formulas
above (`testdata/chain/tx_reference.py`: the PA's 1 dB point and saturation by root-finding Rapp's own AM-AM, the tanh
stages' compression by integrating the describing function); a test holds the budget to it at 10⁻⁶ dB:

| Readout | Value |
|---|---|
| Gain, NF | 42.00 dB, 14.14 dB |
| Output power | 28.00 dBm; as a CW carrier 27.95 dBm, 0.05 dB compressed |
| OP1dB, back-off | 34.04 dBm, 6.04 dB |
| Psat, back-off | 36.40 dBm, 8.40 dB |
| OIP3, ACLR | 44.65 dBm, −38.07 dBc |
| Noise floor | −117.84 dBm/Hz |
| Noise in a 20 MHz band 200 MHz above the carrier | −156.50 dBm/Hz, −83.49 dBm: both filters' rejection, 30.7 and 34.4 dB there |
| Image, LO | 35.17 dB rejected at the modulator, −167.82 dBc at the output; −23.22 dBc at the modulator, −90.27 dBc at the output |

And its benches against it:

| Quantity | Budget | Simulated | Why it differs |
|---|---|---|---|
| Output over the drive, −34 to +4 dBm in | the CW output | 5×10⁻⁴ dB under at every drive | the Chebyshev filter's 0.1 dB ripple at 3.5 GHz, which the budget leaves out |
| Output 18 dB past the operating point | 36.3665 dBm | 36.3659 dBm | as above |
| IP1dB | −6.96 dBm | −6.86 dBm | the cascaded-P1dB formula, as a receive chain's |
| Image, LO at the modulator | −35.17, −23.22 dBc | −35.17, −23.21 dBc | |
| Image, LO at the output | −167.82, −90.27 dBc | −166.3, −90.36 dBc | the image is the run's MaxMixOrder 5 truncation: at 9 it reads −167.88 dBc |
| Noise floor, 1 MHz off the carrier | −117.84 dBm/Hz | −117.98 dBm/Hz | half the noise is amplitude, which the PA at its operating point passes 0.2245 dB under its gain, the other half its CW gain: 0.134 dB expected |
| Noise in the receive band | −156.50 dBm/Hz | −156.63 dBm/Hz | as above |
| PA: AM-AM, AM-PM | Rapp's law | 6.5×10⁻¹⁰ dB, 10⁻¹⁵° | |
| PA: IP1dB | 5.835 dBm | 5.826 dBm | the bench's three-point interpolation |
| IM3 at the operating point | −39.32 dBc | −43.41 dBc | the cubic with the PA's 1 dB point is pessimistic for `P` = 2 away from compression |
| ACLR at the operating point | −38.07 dBc | −39.07 dBc | the estimate is optimistic at the PA (alone: −38.51 estimated, −37.73 and −37.86 simulated), and the band filter after it removes 1.43 dB of the regrowth, which the estimate does not have |
| Output, NR carrier | 28.00 dBm | 27.65 dBm | the carrier's peaks, up to the PA's 1 dB point, compress |
| EVM, NR carrier | — | 4.09 % | the PA's compression of the peaks |

The PA alone, driven by the same NR carrier at its operating point (its own bench), puts out 28.38 dBm with
−37.73 and −37.86 dBc of ACLR and 2.53 % EVM, at 6.88 % efficiency on its 10 W.

## A digital and a polar transmitter {#stream-tx}

The example's `Digital TX.cchain` and `Polar TX.cchain` start from a [stream](#streams): a 20 MHz NR carrier at 15 kHz
subcarrier spacing, its 19.08 MHz occupied bandwidth the chain's, the source at 0 K.

The digital one is zero-IF, −10 dBm on each rail: a `DAC` of 6 bits across ±0.32 V, NRZ at 122.88 MHz (10.10 dB below
full scale); a 5th-order Butterworth low-pass at 49.152 MHz with 0.5 dB of loss, per rail; an `IQMod` at 3.5 GHz, each
mixer −7 dB and IIP3 25 dBm; a driver of 20 dB (OIP3 38 dBm); a Rapp PA of 22 dB, `Psat` 43 dBm, `P` 2; and the linear
transmitter's band filter. The polar one is a `PolarTx` of `Ks` 10, an 80 MHz third-order amplitude path and 1 ns of
mismatch, driven at 0 dBm, then the same band filter. Their budgets, which `testdata/chain/stream_reference.py`
reproduces from the formulas above to 10⁻⁶:

| Readout | Digital TX | Polar TX |
|---|---|---|
| Output | 26.91 dBm; 12.61 dB under OP1dB, 15.49 dB under Psat | 19.40 dBm |
| SNR in the channel | 35.87 dB: the DAC's SQNR, 27.78 dB, and 8.09 dB of processing gain | |
| EVM floor | 1.64 %: the floor, 1.61 %, and the droop, 0.30 % | 1.143 %: the mismatch, 1.143 %, and the amplitude path, 0.018 % |
| ACLR | −36.15 dBc: the DAC's floor, with the intercepts' −51.03 dBc | −41.98 dBc: the mismatch's |
| DAC image | −21.50 dB at the DAC, −60.58 dBc at the output, 113.34 MHz off | |

And their Envelope benches against it, at the operating point:

| Quantity | Budget | Simulated | Why it differs |
|---|---|---|---|
| Digital: output | 26.91 dBm | 26.86 dBm | the NR carrier's peaks, 12.6 dB under the OP1dB |
| Digital: ACLR | −36.15 dBc | −36.33, −36.46 dBc | the DAC's floor alone is −36.29 dBc; the intercepts' −51.03 is the cubic's at the PA's 1 dB point, pessimistic 15 dB back from Psat, where Rapp's IM3 falls at 5:1 (the PA alone reads −64.4 and −64.1 dBc against −52.7) |
| Digital: EVM | 1.64 % (floor) | 1.68 % | the record's own floor, 0.37 % in quadrature (the PA alone reads 0.13 %) |
| Digital: EVM, 3 dB down | 2.29 % | 2.31 % | as above |
| Digital: EVM, 3 dB up | 1.18 % | 1.77 % | 7.1 dB under full scale, the DAC clips; the budget notes it and does not count it |
| Polar: output | 19.40 dBm | 19.40 dBm | 6×10⁻⁴ dB |
| Polar: ACLR | −41.98 dBc | −41.69, −41.70 dBc | the amplitude path's own share, which the leading-order model reads low (below) |
| Polar: EVM | 1.14 % | 1.22 % | 0.42 % in quadrature: the record's own floor and the 80 MHz path at the 2 ns step, as the System Design PolarTx bench reads at the same point |

The polar mismatch's term carries the bench: its in-band and adjacent shares are its model's, and that model's own Monte
Carlo reproduces the System Design PolarTx bench at 1, 2 and 4 ns to 1 % in EVM and 0.4 dB in ACPR1.

## How closely the simulation agrees {#agreement}

Measured on the example's superhet and array, the budget against the simulation of the benches it writes:

| Quantity | Superhet | 4-element array | Why it differs at all |
|---|---|---|---|
| Gain | −1.1×10⁻⁴ dB | −1.1×10⁻⁴ dB | The small-signal drive, 40 dB below IP1dB. An S-parameter bench reads 3×10⁻¹⁰ dB |
| NF, and each stage's | +6.6×10⁻⁵ dB at most | +1.0×10⁻⁴ dB at most | The harmonic-balance noise run's 1 kHz offset from the IF, and its drive |
| IIP3 | +1.8×10⁻⁴ dB | +1.9×10⁻⁴ dB | tanh's fifth-order term, 50 dB below the intercept |
| OIP3, and each stage's gain and OIP3 | 2.6×10⁻⁴ dB at most | 2.6×10⁻⁴ dB at most | as above |
| IP1dB | +0.048 dB | +0.062 dB | The cascaded-P1dB formula is an approximation: the chain compresses a little later |
| SNR | −0.13 dB | −0.27 dB | One noise record's statistics, about ±0.12 dB from seed to seed |

The same array at RF, with no mixer, runs its noise through the S-parameter analysis instead and reads 3×10⁻¹⁰ dB in
gain and 10⁻¹¹ dB in NF. The envelope
SNR is a measurement of one noise record: four seeds of the array read 29.17 to 29.42 dB against 29.37 dB predicted.

The superhet's Levels bench puts the signal on the budget to 2×10⁻⁵ dB at every node and the IM3 to 8×10⁻⁵ dB; its
Blockers bench reads each blocker 0.001 to 0.003 dB below the budget, the blocker's own slight compression at −40 and
−50 dBm into the LNA. The front end's 1-Tone bench reads the canceller's noise, its through arm and the reference path's
coupled noise, on the formula to 10⁻⁶ dB.

The Canceller bench's depth, at each of the three interference carriers and every swept error, is the closed form's to
within 1.4 % of the depth, 0.7 dB at 49.7 dB deep and under 0.3 dB everywhere shallower than 45 dB. With cancellation the
64-QAM carrier's EVM falls from 840 % to 3.2 % and the QPSK one's from 1036 % to 8.6 %.

The superhet's Demo bench reads the SNR 0.20 dB under the budget at its lowest power, where noise dominates, and the
EVM's noise part 57.0 % against the budget's 1/√SNR of 56.2 %. Higher up, two floors the budget does not model take
over: the LO's phase noise, 1.23 % of EVM in the distortion part, which is the mask integrated over the channel
(−38.2 dBc), and a noise part that stops at about 1 % (40 dB of SNR), which the Canceller bench's NR carriers show as
well. At 5 dB past the input P1dB the distortion part is 15.5 %.

The dual-conversion receiver's benches, each conversion's against its own budget:

| Quantity | To IF1 | From IF1 | Why it differs at all |
|---|---|---|---|
| Gain | −1.1×10⁻⁴ dB | −1.1×10⁻⁴ dB | the small-signal drive, as above |
| NF, and each stage's | +3.1×10⁻⁵ dB | +9.9×10⁻⁵ dB | as above |
| IP1dB | +0.033 dB | +0.044 dB | the cascaded-P1dB formula |
| Signal and IM3 at every node | 1.1×10⁻⁵ and 2.5×10⁻⁵ dB | 1.3×10⁻⁵ and 3.4×10⁻⁴ dB | |
| Each blocker at every node | 0.0013 to 0.0025 dB under | 8×10⁻⁵ dB; the input +0.0018 dB | the blockers' compression; the second conversion's input has no isolator in front of its roofing filter |
| SNR, envelope | −0.45 dB | +0.016 dB | the first conversion ends at MIX1's output, 7.5 dB over a noise factor of 1.41, so the load's own noise, which the budget leaves out, is 1/7.9 of what reaches it: 0.52 dB |

The two conversions' simulated gains added, and their noise factors cascaded by Friis, are the whole chain's budget to
−2.2×10⁻⁴ dB and +8×10⁻⁵ dB.
The whole chain's 2-Tone bench, four tones at MaxMixOrder 5, reads IIP3 and OIP3 3×10⁻³ dB high and every stage's OIP3
within 5×10⁻³ dB. Each conversion's Demo reads its SNR at the lowest power on its budget, once the load's noise is
counted (−0.48 dB to MIX1's output, +0.08 dB to the end).

The 26 GHz link's receiver, run at the −59.41 dBm its link delivers to each element, reads −1.1×10⁻⁴ dB in gain and
+2.8×10⁻⁴ dB in NF on its 1-Tone bench, +9×10⁻⁴ dB in IIP3 on its 2-Tone bench, and its eight-element Steering and
Combiners benches 10⁻¹⁴ dB. Its Levels bench drives two tones each at that power, 24 dB under the receiver's input
P1dB, and reads the mixer's output 0.013 dB under the budget, its IM3 0.030 dB: the tones' own slight compression.
Its Envelope bench reads the SNR in the 50 MHz NR carrier's channel 0.02 dB under the budget's 43.15 dB. Its link has
no circuit: EIRP, the path and every readout agree with an independent calculation (`testdata/chain/link_reference.py`)
to 10⁻⁹ at five points. Its TX bench reads the output at 10 dB of overdrive 0.44 dB under `Psat`, and at the 8 dB
back-off 0.92 dB under the budget's `Psat − Backoff − Loss`, at 6.3 % EVM and −30.3 dBc ACPR.

The array's Steering bench puts its pattern on the closed-form array factor to 3×10⁻⁹ dB within 3 dB of each peak and
to 10⁻⁶ dB down to −60 dB, and its gain, NF and array gain at each peak on the budget to 10⁻¹⁴ dB. The Combiners bench
reads each variant's gain and NF to 10⁻¹⁴ dB; each variant's intercepts and compression point, transferred on its own,
read as the table above does.

The digital and polar transmitters' Envelope benches are tabled with them, in
[A digital and a polar transmitter](#stream-tx): the output within 0.05 dB, the ACLR within 0.35 dB, and the EVM 0.4 %
over the budget's floor in quadrature, the bench's own.

The linear transmitter's benches are tabled with it, in [A linear transmitter](#linear-tx): its output over 38 dB of
drive on the budget's CW output to 5×10⁻⁴ dB, its PA's AM-AM and AM-PM on Rapp's law to 10⁻⁹, its modulator's image and
LO on their closed forms, its noise 0.13 to 0.15 dB under the budget (0.134 dB expected), its two-tone IM3 4.1 dB under
the estimate, and its ACLR 0.8 dB over the estimate at the PA and 1.0 dB under it at the output, where the band filter
takes 1.43 dB of the regrowth.

## The .cchain file {#format}

A `.cchain` is JSON. The system inputs are numbers whose names carry their unit; a stage's parameters are text, exactly
as the part's netlist line carries them.

```json
{
  "FormatVersion": 1,
  "FrequencyHz": 2450000000,
  "BandwidthHz": 4500000,
  "InputPowerDbm": -70,
  "TemperatureK": 290,
  "RequiredSnrDb": 10,
  "Stages": [
    { "Name": "LNA", "Part": "Amp", "Parameters": { "Gain": "16", "NF": "1", "IP3": "15", "IP3Ref": "Output" } },
    { "Name": "RFBPF", "Part": "Filter", "Parameters": { "Response": "Butterworth", "F1": "2.35 GHz", "F2": "2.55 GHz", "IL": "1.5" } },
    { "Name": "MIX", "Part": "Mixer", "Parameters": { "ConvGain": "-7", "NF": "7", "IIP3": "10", "Plo": "7", "Flo": "2.1 GHz" } }
  ]
}
```

| Key | Meaning |
|---|---|
| `FormatVersion` | 1; 2 for a chain that uses a value of the frequency, a budget-only parameter, a `Levels` combiner, a link, a sweep or a readout input; 3 for a transmit chain, an IQ modulator, a DAC, a polar transmitter, an Amplifier's `Law`, `Psat`, `P`, `PhiSat` or `PhiMax`, or a converter's `Sideband`. A file from a newer circuitRF is refused, not read in part. |
| `Direction` | Optional: `"Transmit"` for a [transmit chain](#transmit). Absent: a receive chain. |
| `RxBandOffsetHz`, `RxBandHz` | Optional: a transmit chain's receive band, its centre's offset from the carrier at the output and its width. |
| `FrequencyHz`, `BandwidthHz`, `InputPowerDbm`, `TemperatureK`, `RequiredSnrDb` | The [system inputs](#inputs). Absent: 2.45 GHz, 1 MHz, −70 dBm, 290 K, 10 dB. |
| `Stages` | In signal order. `Name` follows the instance-name rule (a letter, then letters, digits and `_`). |
| `Part` | `Amp`, `Mixer`, `IQMod`, `DAC`, `PolarTx`, `Atten`, `Filter`, `Switch`, `Circulator`, `Coupler`, `PhaseShifter`, `Limiter`, `Canceller`, `Splitter`, `SnP` or `Combiner`. |
| `Parameters` | The part's parameters by their own names. An absent one takes the part's default (a System block's own placement default); one the part does not list is carried onto the schematic unchanged and not read by the budget. |
| `Blockers` | Optional: `[{"OffsetHz": 150000000, "LevelDbm": -40}, …]`, each an offset from `FrequencyHz` and a level at the input. |
| `Demo` | Optional: `true` to write the [demo bench](#demo) too. |
| `FractionalBandwidth`, `Streams`, `NfAllowanceDb`, `RxBackoffDb` | Optional: the [readouts' inputs](#readouts). Absent: the stated bandwidth, 1, 1 dB, 10 dB. |
| `Link` | Optional: `{"Tx": {"Psat": "…", …}, "Path": {"Distance": "200 m", …}}`, the [link](#link)'s inputs by name, as a stage's parameters are. |
| `Sweeps` | Optional: `[{"Of": "Frequency", "From": 20e9, "To": 40e9, "Points": 41}, …]`, `Of` one of `Frequency`, `TxElements`, `RxElements`. See [Sweeps](#sweeps). |

A chain has at most one Combiner. An `SnP` stage's `File` is a path; a relative one resolves from the workspace the
`.cchain` belongs to, or its own folder when it belongs to none, and so does a [table](#scaling)'s.

## From the command line {#cli}

```text
circuitrf chain Superhet.cchain                      the cascade, as the window shows it
circuitrf chain Superhet.cchain -o budget.npy        the budget as a DataSet: per-stage cubes over a stage axis
circuitrf chain "26 GHz Link.cchain" -o link.npy     the link's numbers as scalars, each sweep a group of its own
circuitrf chain Superhet.cchain --level-diagram levels.svg   the level diagram, as a .svg or a .png
circuitrf chain Superhet.cchain --to-schematic       the benches and their displays, beside the .cchain
circuitrf hb "Superhet 2-Tone/schematic/Superhet 2-Tone.csch"
```

A transmit chain's report reads its output in place of a receiver's sensitivity: the output power and its CW
compression, the back-off from OP1dB and from Psat, the ACLR, the noise floor and the receive band's noise, and an IQ
modulator's image and LO. A chain from a [stream](#streams) adds a line: its EVM floor, its ACLR with the stream's share,
and a DAC's image; the line before keeps the estimate from the OIP3 alone.

`--to-schematic [dir]` writes into another folder, `--name` names the benches, `--demo` adds the demo bench, and an
existing bench is a refusal unless `--replace` is given. `circuitrf check` reads a `.cchain` through the same budget and reports its refusals. Over the MCP
server the verb is `run analysis=chain`.

<p class="small">See also: <a href="system-components.html">System Components</a> ·
<a href="simulations.html#noise">Simulations ▸ Noise</a> ·
<a href="measurements.html">Measurements</a> ·
<a href="data-display.html">Data Display</a>.</p>
