/*  aethernr.cpp

This file is part of a program that implements a Software-Defined Radio.

The AetherSDR noise reduction filters (NR2, RN2, NR4, DFNR) behind the C
interface in aethernr.h, for mono audio inside the WDSP receive chain.

The processing follows AetherSDR (src/core: SpectralNR, RNNoiseFilter,
SpecbleachFilter, DeepFilterFilter): the same filter geometry, scaling,
parameter defaults and NR4 noise-learning period.  AetherSDR works on 24 or
48 kHz stereo audio from a FlexRadio; here each filter sees WDSP's mono
receive audio, so the stereo adapters and 24 kHz resamplers are not needed.

Copyright (C) 2024-2026 AetherSDR Contributors (the filters)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

*/

#include "aethernr.h"
#include "aethernr_support.h"
#include "SpectralNR.h"
#include "core/dsp/FftwPlannerLock.h"

#include "rnnoise.h"
#include "specbleach_denoiser.h"
#ifdef HAVE_DFNR
#include "deep_filter.h"
#endif

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <cmath>
#include <deque>
#include <memory>
#include <mutex>
#include <string>
#include <sys/stat.h>
#include <vector>

namespace {

std::mutex g_modelDirMutex;
std::string g_modelDir;

constexpr const char* kDfnrModelFile = "DeepFilterNet3_onnx.tar.gz";

std::string dfnrModelPath()
{
    std::lock_guard<std::mutex> lock(g_modelDirMutex);
    if (g_modelDir.empty()) return {};
    std::string path = g_modelDir;
    if (path.back() != '/') path += '/';
    path += kDfnrModelFile;
    struct stat st{};
    return stat(path.c_str(), &st) == 0 ? path : std::string();
}

/// Common base: every filter processes mono float blocks of any length.
struct Filter
{
    int type = 0;
    int rate = 48000;
    // AETHERNR_DEBUG=1: log input/output RMS every 2 s (per filter)
    double dbgIn = 0, dbgOut = 0;
    long dbgCount = 0;
    virtual ~Filter() = default;
    virtual bool valid() const = 0;
    virtual void reset() = 0;
    virtual void setParam(int param, double value) = 0;
    virtual void process(const float* in, float* out, int n) = 0;
};

/// Frame-based filters (RN2, DFNR): collect whole frames, process them, and
/// hand the output back one frame late so every call returns 'n' samples.
class Framer
{
public:
    void configure(int frame) { m_frame = frame; reset(); }
    void reset()
    {
        m_in.clear();
        m_out.assign(static_cast<std::size_t>(m_frame), 0.0f);   // one frame of latency
        m_buf.assign(static_cast<std::size_t>(m_frame), 0.0f);
    }
    template <class F>
    void run(const float* in, float* out, int n, F&& processFrame)
    {
        m_in.insert(m_in.end(), in, in + n);
        while (static_cast<int>(m_in.size()) >= m_frame)
        {
            std::copy(m_in.begin(), m_in.begin() + m_frame, m_buf.begin());
            m_in.erase(m_in.begin(), m_in.begin() + m_frame);
            std::vector<float> frameOut(static_cast<std::size_t>(m_frame));
            processFrame(m_buf.data(), frameOut.data());
            m_out.insert(m_out.end(), frameOut.begin(), frameOut.end());
        }
        const int have = static_cast<int>(m_out.size());
        const int take = std::min(have, n);
        std::copy(m_out.begin(), m_out.begin() + take, out);
        m_out.erase(m_out.begin(), m_out.begin() + take);
        std::fill(out + take, out + n, 0.0f);
    }

private:
    int m_frame = 480;
    std::deque<float> m_in, m_out;
    std::vector<float> m_buf;
};

// ---------------------------------------------------------------- NR2 ----

class Nr2 final : public Filter
{
public:
    explicit Nr2(int rate)
    {
        // AudioEngine::createNr2Filter: the improved 1024/4 geometry at 24 kHz,
        // scaled to the producer rate.
        constexpr int kFftAt24k = 1024, kOverlap = 4;
        m_nr = std::make_unique<AetherSDR::SpectralNR>(kFftAt24k * rate / 24000, rate, kOverlap, false);
        // Nr2SettingsModel defaults
        m_nr->setGainMethod(2);
        m_nr->setNpeMethod(0);
        m_nr->setAeFilter(true);
        m_nr->setGainMax(1.0f);
        m_nr->setGainFloor(0.0f);
        m_nr->setGainSmooth(0.85f);
        m_nr->setQspp(0.20f);
        m_nr->setPost2Run(false);
        m_nr->setPost2Factor(0.15f);
        m_nr->setPost2Nlevel(0.15f);
        m_nr->setPost2TaperHz(2871.0f);
        m_nr->setPost2DecaySeconds(5.0f);
    }
    bool valid() const override { return m_nr && !m_nr->hasPlanFailed(); }
    void reset() override { m_nr->reset(); }
    void setParam(int p, double v) override
    {
        const float f = static_cast<float>(v);
        switch (p)
        {
        case AETHERNR_NR2_GAIN_METHOD: m_nr->setGainMethod(static_cast<int>(v)); break;
        case AETHERNR_NR2_NPE_METHOD: m_nr->setNpeMethod(static_cast<int>(v)); break;
        case AETHERNR_NR2_AE_FILTER: m_nr->setAeFilter(v != 0); break;
        case AETHERNR_NR2_GAIN_MAX: m_nr->setGainMax(f); break;
        case AETHERNR_NR2_GAIN_FLOOR: m_nr->setGainFloor(f); break;
        case AETHERNR_NR2_GAIN_SMOOTH: m_nr->setGainSmooth(f); break;
        case AETHERNR_NR2_QSPP: m_nr->setQspp(f); break;
        case AETHERNR_NR2_POST2_RUN: m_nr->setPost2Run(v != 0); break;
        case AETHERNR_NR2_POST2_FACTOR: m_nr->setPost2Factor(f); break;
        case AETHERNR_NR2_POST2_NLEVEL: m_nr->setPost2Nlevel(f); break;
        case AETHERNR_NR2_POST2_TAPER_HZ: m_nr->setPost2TaperHz(f); break;
        case AETHERNR_NR2_POST2_DECAY_S: m_nr->setPost2DecaySeconds(f); break;
        default: break;
        }
    }
    void process(const float* in, float* out, int n) override { m_nr->process(in, out, n); }

private:
    std::unique_ptr<AetherSDR::SpectralNR> m_nr;
};

// ---------------------------------------------------------------- RN2 ----

class Rn2 final : public Filter
{
public:
    Rn2()
    {
        m_st = rnnoise_create(nullptr);
        m_framer.configure(rnnoise_get_frame_size());
    }
    ~Rn2() override { if (m_st) rnnoise_destroy(m_st); }
    bool valid() const override { return m_st != nullptr; }
    void reset() override
    {
        if (m_st) rnnoise_destroy(m_st);
        m_st = rnnoise_create(nullptr);
        m_framer.reset();
    }
    void setParam(int p, double v) override
    {
        if (p == AETHERNR_RN2_DRY_MIX) m_dryMix = static_cast<float>(std::clamp(v, 0.0, 0.5));
    }
    void process(const float* in, float* out, int n) override
    {
        m_framer.run(in, out, n, [this](float* frame, float* result) {
            const int fs = rnnoise_get_frame_size();
            // RNNoise expects [-32768, 32768] (RNNoiseFilter)
            for (int i = 0; i < fs; i++) frame[i] *= 32768.0f;
            if (m_dryMix > 0.0f) rnnoise_process_frame_with_dry_mix(m_st, result, frame, m_dryMix);
            else rnnoise_process_frame(m_st, result, frame);
            for (int i = 0; i < fs; i++) result[i] /= 32768.0f;
        });
    }

private:
    DenoiseState* m_st = nullptr;
    Framer m_framer;
    float m_dryMix = 0.0f;
};

// ---------------------------------------------------------------- NR4 ----

class Nr4 final : public Filter
{
public:
    explicit Nr4(int rate) : m_rate(rate)
    {
        constexpr float kFrameSizeMs = 40.0f;              // SpecbleachFilter
        {
            auto lock = AetherSDR::fftwPlannerLock();
            m_handle = specbleach_initialize(static_cast<uint32_t>(rate), kFrameSizeMs);
        }
        if (m_handle) m_latency = static_cast<int>(specbleach_get_latency(m_handle));
        m_params.learn_noise = 0;
        m_params.residual_listen = false;
        m_params.reduction_amount = 10.0f;
        m_params.smoothing_factor = 0.0f;
        m_params.whitening_factor = 0.0f;
        m_params.adaptive_noise = 1;
        m_params.noise_estimation_method = 0;
        m_params.masking_depth = 0.5f;
        m_params.suppression_strength = 0.5f;
        m_params.aggressiveness = 0.0f;
        m_params.tonal_reduction = 0.0f;
        m_dirty = true;
        resetDelay();
    }
    ~Nr4() override
    {
        if (m_handle)
        {
            auto lock = AetherSDR::fftwPlannerLock();
            specbleach_free(m_handle);
        }
    }
    bool valid() const override { return m_handle != nullptr; }
    void reset() override
    {
        m_learned = 0;
        resetDelay();
        if (m_handle) specbleach_reset_noise_profile(m_handle);
    }
    void setParam(int p, double v) override
    {
        const float f = static_cast<float>(v);
        switch (p)
        {
        case AETHERNR_NR4_REDUCTION_DB: m_params.reduction_amount = std::clamp(f, 0.0f, 40.0f); break;
        case AETHERNR_NR4_SMOOTHING_PCT: m_params.smoothing_factor = std::clamp(f, 0.0f, 100.0f); break;
        case AETHERNR_NR4_WHITENING_PCT: m_params.whitening_factor = std::clamp(f, 0.0f, 100.0f); break;
        case AETHERNR_NR4_ADAPTIVE: m_params.adaptive_noise = v != 0 ? 1 : 0; break;
        case AETHERNR_NR4_NOISE_METHOD: m_params.noise_estimation_method = std::clamp(static_cast<int>(v), 0, 2); break;
        case AETHERNR_NR4_MASKING_DEPTH: m_params.masking_depth = std::clamp(f, 0.0f, 1.0f); break;
        case AETHERNR_NR4_SUPPRESSION: m_params.suppression_strength = std::clamp(f, 0.0f, 1.0f); break;
        default: return;
        }
        m_dirty = true;
    }
    void process(const float* in, float* out, int n) override
    {
        if (m_dirty) { specbleach_load_parameters(m_handle, m_params); m_dirty = false; }
        specbleach_process(m_handle, static_cast<uint32_t>(n), in, out);
        // While the noise profile is being learnt (about a second), pass the
        // input through, delayed by the filter's latency so the switch to the
        // processed signal is seamless (SpecbleachFilter + MonoDspStereoAdapter).
        // AetherSDR counts 25 of its ~40 ms blocks; WDSP's blocks are larger
        // (4096 samples here), so the second is counted in samples.
        const bool learning = m_learned < m_rate;
        for (int i = 0; i < n; i++)
        {
            const float dry = m_delay[m_delayPos];
            m_delay[m_delayPos] = in[i];
            m_delayPos = (m_delayPos + 1) % static_cast<int>(m_delay.size());
            if (learning) out[i] = dry;
        }
        if (learning) m_learned += n;
    }

private:
    void resetDelay()
    {
        m_delay.assign(static_cast<std::size_t>(std::max(1, m_latency)), 0.0f);
        m_delayPos = 0;
    }
    SpectralBleachHandle m_handle = nullptr;
    SpectralBleachDenoiserParameters m_params{};
    bool m_dirty = true;
    int m_latency = 0;
    int m_rate;
    long m_learned = 0;                                  // samples seen while learning
    std::vector<float> m_delay;
    int m_delayPos = 0;
};

// --------------------------------------------------------------- DFNR ----

#ifdef HAVE_DFNR
class Dfnr final : public Filter
{
public:
    Dfnr() { create(); }
    ~Dfnr() override { if (m_state) df_free(m_state); }
    bool valid() const override { return m_state != nullptr; }
    void reset() override
    {
        if (m_state) df_free(m_state);
        m_state = nullptr;
        create();
    }
    void setParam(int p, double v) override
    {
        if (p == AETHERNR_DFNR_ATTEN_LIMIT_DB) m_atten = static_cast<float>(std::clamp(v, 0.0, 100.0));
        else if (p == AETHERNR_DFNR_POST_FILTER_BETA) m_beta = static_cast<float>(std::clamp(v, 0.0, 0.3));
        else return;
        m_dirty = true;
    }
    void process(const float* in, float* out, int n) override
    {
        if (m_dirty)
        {
            df_set_atten_lim(m_state, m_atten);
            df_set_post_filter_beta(m_state, m_beta);
            m_dirty = false;
        }
        m_framer.run(in, out, n, [this](float* frame, float* result) {
            df_process_frame(m_state, frame, result);
        });
    }

private:
    void create()
    {
        const std::string path = dfnrModelPath();
        if (path.empty())
        {
            aethernr::logWarning("DFNR: model DeepFilterNet3_onnx.tar.gz not found");
            return;
        }
        m_state = df_create(path.c_str(), m_atten, nullptr);
        if (!m_state)
        {
            aethernr::logWarning("DFNR: df_create() failed for " + path);
            return;
        }
        m_framer.configure(static_cast<int>(df_get_frame_length(m_state)));
        m_dirty = true;
    }
    DFState* m_state = nullptr;
    Framer m_framer;
    float m_atten = 100.0f;         // DeepFilterFilter defaults
    float m_beta = 0.0f;
    bool m_dirty = true;
};
#endif

} // namespace

extern "C" {

AETHERNR_API void aethernr_set_model_dir(const char* dir)
{
    std::lock_guard<std::mutex> lock(g_modelDirMutex);
    g_modelDir = dir ? dir : "";
}

AETHERNR_API int aethernr_available(int type, int rate)
{
    switch (type)
    {
    case AETHERNR_NR2:
    case AETHERNR_NR4:
        return rate >= 8000 ? 1 : 0;
    case AETHERNR_RN2:
        return rate == 48000 ? 1 : 0;
    case AETHERNR_DFNR:
#ifdef HAVE_DFNR
        return rate == 48000 && !dfnrModelPath().empty() ? 1 : 0;
#else
        return 0;
#endif
    default:
        return 0;
    }
}

AETHERNR_API void* aethernr_create(int type, int rate)
{
    if (!aethernr_available(type, rate)) return nullptr;
    std::unique_ptr<Filter> f;
    try
    {
        switch (type)
        {
        case AETHERNR_NR2: f = std::make_unique<Nr2>(rate); break;
        case AETHERNR_RN2: f = std::make_unique<Rn2>(); break;
        case AETHERNR_NR4: f = std::make_unique<Nr4>(rate); break;
#ifdef HAVE_DFNR
        case AETHERNR_DFNR: f = std::make_unique<Dfnr>(); break;
#endif
        default: return nullptr;
        }
    }
    catch (...)
    {
        return nullptr;
    }
    if (!f->valid()) return nullptr;
    f->type = type;
    f->rate = rate;
    return f.release();
}

AETHERNR_API void aethernr_destroy(void* filter) { delete static_cast<Filter*>(filter); }

AETHERNR_API void aethernr_reset(void* filter)
{
    if (filter) static_cast<Filter*>(filter)->reset();
}

AETHERNR_API void aethernr_set_param(void* filter, int param, double value)
{
    if (filter) static_cast<Filter*>(filter)->setParam(param, value);
}

AETHERNR_API void aethernr_process(void* filter, const float* in, float* out, int n)
{
    if (!filter || n <= 0) return;
    Filter* f = static_cast<Filter*>(filter);
    f->process(in, out, n);
    static const bool debug = std::getenv("AETHERNR_DEBUG") != nullptr;
    if (debug)
    {
        for (int i = 0; i < n; i++) { f->dbgIn += in[i] * in[i]; f->dbgOut += out[i] * out[i]; }
        f->dbgCount += n;
        if (f->dbgCount >= 2L * f->rate)
        {
            static const char* names[] = { "", "NR2", "RN2", "NR4", "DFNR" };
            std::fprintf(stderr, "aethernr: %s at %d Hz, blocks of %d: in %.1f dBFS out %.1f dBFS (%+.1f dB)\n",
                         f->type >= 1 && f->type <= 4 ? names[f->type] : "?", f->rate, n,
                         10 * std::log10(f->dbgIn / f->dbgCount + 1e-20), 10 * std::log10(f->dbgOut / f->dbgCount + 1e-20),
                         10 * std::log10((f->dbgOut + 1e-20) / (f->dbgIn + 1e-20)));
            f->dbgIn = f->dbgOut = 0;
            f->dbgCount = 0;
        }
    }
}

} // extern "C"
