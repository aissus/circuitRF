---
title: AN-05 — A fast stand-in for a slow device
slug: app-notes/an05-stand-in.html
doc-kind: Application Note
breadcrumb: Docs > App Notes > AN-05 A fast stand-in for a slow device
lede: A compiled transistor model can be the slowest part of a loadpull. At one bias region, one parameter set and one frequency, circuitRF can sample it into a table model of itself, a stand-in that runs in-process and in parallel and answers for any source and load. How to make one, how wide to sample, what it keeps and loses, and how much faster and how close it is on four devices.
keywords: stand-in, model sample, quasi-static, table model, surrogate, compiled model, Verilog-A, OSDI, loadpull speed, harmonicaRF, Model Composer, SimulatedTables, validation, HBT, VBIC, thermal node, isothermal, Rth, domain, extrapolation, application note
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#when">When this note applies</a></li>
<li><a href="#flow">The flow</a></li>
<li><a href="#grid">How wide, and how fine</a></li>
<li><a href="#keeps">What it keeps and what it loses</a></li>
<li><a href="#thermal">Self-heating</a></li>
<li><a href="#hbt">A device that is not a FET</a></li>
<li><a href="#speed">How much faster</a></li>
<li><a href="#accuracy">How close</a></li>
<li><a href="#checklist">Checklist</a></li>
</ol>
</nav>

## When this note applies {#when}

A loadpull asks the same transistor the same questions thousands of times: a grid of loads, each driven up step by step
into compression, each step a harmonic-balance solve of several Newton iterations. When the transistor is a compiled
compact model, every one of those evaluations runs in a separate worker process, one call at a time, through a module
with internal nodes of its own. That is often where the time goes.

A **stand-in** replaces the device with a table model of itself: currents and charges sampled by circuitRF from the
device it already runs, fitted by [Model Composer](../reference/model-composer.html#stand-in), exported as a `.cmodel`
and placed as a `ComposedFET`. It runs inside circuitRF, so a loadpull grid can solve its loads in parallel, and it has
two or three unknowns where a compiled module has many. It is valid at the bias region, the model parameters and the
frequency it was sampled at. Inside that region it answers for any source and load, which is what loadpull and
[harmonicaRF](../reference/harmonicarf.html) ask.

It is a **device-level model**, a `.cmodel` with currents, charges and optionally a thermal network, not a behavioural
model of the amplifier. Everything that runs a `ComposedFET` runs it unchanged.

## The flow {#flow}

| Step | Command | What happens |
|---|---|---|
| Sample | `model sample pa.cmc --from pa.charm --vgs -20,12,0.5 --vds -4,116,2` | DC and S at every bias of the grid, on the device's own terminals. Written as `sampled/tables.npy` and imported as a Simulated tables set. With a `.charm`, the device is also load-pulled over five loads about the document's load, into a Large signal set. Current and Charge are set to tables. |
| Fit | `model fit pa.cmc --step all` | The tables, splined through the samples themselves. |
| Export | `model export pa.cmc -o pa.cmodel` | The stand-in, with what it was sampled from in its provenance. |
| Validate | `model validate pa.cmc --model pa.cmodel` | The stand-in load-pulled over the device's own loads and compared at compression: Pout, Gt, PAE, drain efficiency, AM–PM and Zin. |

The `.charm` supplies the device (its DUT; the package stays in the document), the bias, the fundamental and the
terminations, so the loadpull the stand-in is judged by is the one harmonicaRF would run. Without one, `--device` takes
an instance line on the nets `g`, `d` and `0` and `--freq` the small-signal frequency. Then there is no loadpull to
validate against, so run the device's own `lp` and import its `.spl` as a Large signal set.

Then place it where the device was: in a schematic as `ComposedFET:X1 g d s File="pa.cmodel"`, or in harmonicaRF with
**Set DUT ▸ Model Composer**.

## How wide, and how fine {#grid}

**Sample the swing, not the bias.** Inside its grid the stand-in is the samples; outside, it is continued linearly, as any
table is. The drain swings to about twice its supply, and, unless a diode clamps it, the gate swings both ways by
several volts. The routine gate of this feature is the Hero-2 GaN-like FET: a gate that is a 50 Ω resistor, at
−3.05 V and 48 V. Sampled over its DC range, `vgs` −8 to 2 V, 41 % of the stand-in's evaluations in a loadpull fall
outside the table, and at compression it misses by 0.51 dB in Pout and a Zin VSWR of 1.72. Sampled from −20 to 12 V,
the same 1 V step gives 0.10 dB and 1.02.

A run that leaves the sampled region **warns once**, naming the region and the share of evaluations outside it, and
`model validate` repeats it beside the loadpull it happened in. A share of a few per cent is Newton iterates straying
on their way to a solution. Tens of per cent is a swing that lives out there: widen the grid.

**Fine where the device bends.** A table is a cubic spline through the samples, so it follows a curve as well as the
samples resolve it. A FET's knee and pinch-off want a few tenths of a volt; an exponential diode or junction wants a few
millivolts where it conducts. The Current step reports the step it sees and the swing below which low-level
intermodulation is unreliable. Points cost little: sampling is seconds for an in-process device, and a few seconds to
a minute for a compiled one, done once.

## What it keeps and what it loses {#keeps}

It keeps the device's DC currents at every sampled point exactly, its small-signal behaviour at the sampled frequency,
and its large-signal behaviour wherever the device is quasi-static: currents and charges that are functions of the
instantaneous terminal voltages.

It loses:

- **Everything that is not quasi-static.** Internal nodes collapse into the two-port: a gate resistance with its
  charge, an inductance, a distributed base, a transit-time excess phase, a non-quasi-static network. Their phase at the
  working frequency and its harmonics is gone. On the shipped GaN example, whose network is inside the compiled
  module, this is the largest error below. To keep a known network, create the document with it first
  (`model new pa.cmc --kind active --network …`) and type its values; `sample` locks them and the samples are then
  de-embedded through it.
- **Charges that are not charges.** The charges come from the low-frequency Y, `Im Y / ω`. When series resistances are
  inside the sampled device those are not exactly derivatives of two charge functions, and the Charge step's two
  integration paths disagree: it reports by how much. A few per cent is worth refitting the charges as an ANN, which
  finds the nearest charges that conserve charge: `model fit pa.cmc --step charge --method ANN`.
- **Low-level distortion from a table.** A cubic table has a piecewise-constant third derivative, so it cannot predict
  high-order intermodulation at very low drive. Refit that constituent as an ANN where it matters.
- **Traps**, unless they are sampled as the [reference describes](../reference/model-composer.html#trap-data). A
  quasi-static sample gives each bias its DC trap state.
- **Scaling.** A stand-in is one instance. Placing it with `N` other than `N0` is refused; sample the size you use.

## Self-heating {#thermal}

When the device's instance line names a fourth net `t` (its thermal node), `sample` holds it at 0 K rise while it
samples, so the tables are isothermal. It then lets it go to the device's own thermal network at the bias and reads
`Rth` as the rise over the power at the terminals. With two or more `--temps` that `Rth` is typed on the Thermal step,
self-heating on. Type `Cth` (or the time constant) there yourself before fitting.

The stand-in's `Rth` is not quite the module's. A compact model heats with its core's power; the stand-in's core is the
whole device, so it heats with the terminals' power, which also includes what the series resistances dissipate. On the
GaN module the module's own 25 K/W reads 22.8 K/W at its hottest sample, and with that value the stand-in's DC with
self-heating is within 0.22 % of the original's.

A device whose thermal node is not reachable is sampled as it heats itself; its tables then carry its static heating,
and the stand-in's provenance says it was not isothermal.

## A device that is not a FET {#hbt}

The instance line maps the terminals. For an HBT whose module is `c b e`, `--device 'VerilogA:Q1 d g 0 File=…'` puts the
base on `g`, the collector on `d` and the emitter on `0`. In the stand-in, `vgs` is then `Vbe`, `vds` is `Vce`, the
drain current is the collector current, the gate current is the base current, and the two charges are the base and
collector charges. Drain–source symmetry stays off, and the equation templates, which are FET laws, do not apply. The
base–emitter junction is exponential, so sample `Vbe` in steps of about 5 mV where it conducts.

Tried on a VBIC 1.3 model compiled from Verilog-A, with a generic parameter set (`IS` 1e-16 A, β 100, `RBX` 10 Ω,
`RBI` 20 Ω, `RCX` 5 Ω, `RCI` 10 Ω, `RE` 2 Ω, `CJE` 50 fF, `CJC` 20 fF, `TF` 2 ps). Its five terminals were placed
`d g 0 0 t`, so the substrate went to the emitter and `dt` to the thermal node. `Vbe` ran from −1 to 1.1 V in 5 mV steps
and `Vce` from −0.5 to 7 V in 0.1 V, 31,996 points sampled isothermally in 45 s; `Rth` read back as the model's own
300 K/W. Load-pulled at 2 GHz, `Vbe` 0.82 V and `Vce` 3 V over 13 loads about 200 Ω, at 3 dB compression the stand-in
was within 0.11 dB of the device in Pout (0.17 at worst) and 1.7 points in PAE, but 1.0 dB in Gt and 5.6° in AM–PM. The
miss is on the input side, in the charges. With the base and collector resistances inside the module, the two
integration paths of the Charge step disagree by 8 % for the base charge and 33 % for the collector charge, and the
table takes their mean. That is the case for refitting the charges as an ANN, or for typing the resistances into the
document so the core is the junctions alone. The stand-in ran the 13 loads in 1.24 s against the device's 1.73 s, and
in 0.82 s on the parallel grid.

## How much faster {#speed}

Each device was load-pulled with `circuitrf lp` over the 20 loads of the Hero-3 grid at 2 GHz, each driven to 3 dB of Gt
compression, with the stand-in placed on the same bench. The times include starting the process (0.33 s). They are the
shortest of several runs on a shared machine, so read the ratios rather than the seconds. The last column has the
parallel loadpull grid: it runs a stand-in's loads side by side and keeps a compiled model on its one worker.

| Device | Device | Stand-in | Parallel grid: device / stand-in |
|---|---|---|---|
| The GaN example as compiled Verilog-A (its network, traps at their DC values, ANN constituents) | 4.14 s | 1.81 s | 3.91 / 0.92 s |
| The same model as its own `ComposedFET` | 3.78 s | | 2.42 s |
| A closed-form FET, compiled | 0.81 s | 1.81 s | 0.77 / 0.93 s |
| Hero-2's SDD FET, in-process | 0.83 s | 2.00 s | 0.60 / 1.02 s |

Against the compiled GaN the stand-in is **2.3× faster**, and **4.3×** on the parallel grid. Against a model that costs
next to nothing per evaluation, a closed form whether compiled or in-process, it is **slower**: its tables and their
extrapolation cost more than the formula. Most of the gain comes from the device's own cost per evaluation, not from the
worker's round trip; the compiled GaN and its `ComposedFET` twin cost about the same. **Make a stand-in for a device that
is expensive to evaluate**: a compact model with many internal nodes, or one built from networks.

Sampling the compiled GaN took 17 s for 5,265 points, one at a time, once.

## How close {#accuracy}

The same loadpulls, compared at 3 dB of Gt compression at every load both reach it at: the stand-in minus the device,
RMS over the loads, the largest in brackets.

| Device | Grid | Pout | Gt | PAE | AM–PM |
|---|---|---|---|---|---|
| Closed-form FET, compiled | 141 × 73 | 0.005 (0.010) dB | 0.0004 (0.002) dB | 0.04 (0.09) pp | 0.07 (0.21)° |
| Hero-2's SDD FET | 65 × 61 | 0.17 (0.45) dB | 0.17 (0.38) dB | 0.92 (2.0) pp | 0.08 (0.20)° |
| The GaN example, compiled, charges refitted as an ANN | 81 × 65 | 0.62 (2.1) dB | 0.08 (0.29) dB | 1.8 (5.7) pp | 0.90 (2.2)° |

The first two devices are quasi-static by construction, and the stand-in reproduces them to within the drive step. The
GaN module carries its own network inside, which the stand-in collapses: its error is the phase of that network at
2 GHz and its harmonics, not the tables. `model validate` runs exactly this comparison against the device's own
loadpull, so you see which case you are in before you use the stand-in.

## Checklist {#checklist}

- The device is expensive to evaluate; a closed-form model is faster as it is.
- The bias, model parameters and frequency are the ones the circuit uses.
- The grid spans the RF swing at compression, not only the DC bias range, and no run warns that it left the region.
- The grid is fine where the device bends: tenths of a volt at a FET's knee, millivolts across a junction.
- A thermal node, when there is one, is wired as `t`, and `Cth` is typed before fitting.
- A network the device carries inside is typed into the document when its phase matters at the working frequency.
- The Charge step's two paths agree; when they do not, the charges are refitted as an ANN.
- `model validate` passes against the device's own loadpull.
