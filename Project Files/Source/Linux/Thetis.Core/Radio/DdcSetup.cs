/*  DdcSetup.cs

This file is part of a program that implements a Software-Defined Radio.

Receive-side port of Console.UpdateDDCs() and Console.UpdateAAudioMixerStates():
decides which DDCs (digital down-converters) in the radio carry receiver 1/2,
at what sample rate, and which receiver audio streams are mixed to the output.

Only the receive (not MOX), no-PureSignal, no-diversity branches are ported
for now; the values are copied unchanged from console.cs.

Copyright (C) 2000-2025 Original authors
Copyright (C) 2020-2026 Richard Samphire MW0LGE

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

namespace Thetis.Radio
{
    internal static unsafe class DdcSetup
    {
        /// <summary>ADC assignment control words (console defaults; set per model in Setup on Windows).</summary>
        public const int RxAdcCtrl1 = 4;
        public const int RxAdcCtrl2 = 0;
        public const int RxAdcCtrlP1 = 4;

        public static void UpdateDDCs(HPSDRModel model, int rx1_rate, int rx2_rate, bool rx2_enabled)
        {
            int DDCEnable = 0;
            const int DDC0 = 1, DDC1 = 2, DDC2 = 4, DDC3 = 8;
            int SyncEnable = 0;
            int[] Rate = new int[8];
            int P1_DDCConfig = 0;
            const int P1_diversity = 0;
            int P1_rxcount = 0;
            int nddc = 0;
            int cntrl1 = 0;
            int cntrl2 = 0;
            bool p1 = NetworkIO.CurrentRadioProtocol == RadioProtocol.USB;

            switch (model)
            {
                case HPSDRModel.ANAN100D:
                case HPSDRModel.ANAN200D:
                case HPSDRModel.ORIONMKII:
                case HPSDRModel.ANAN7000D:
                case HPSDRModel.ANAN8000D:
                case HPSDRModel.ANAN_G2:
                case HPSDRModel.ANAN_G2_1K:
                case HPSDRModel.ANVELINAPRO3:
                    P1_rxcount = 5;                     // RX5 used for puresignal feedback
                    nddc = 5;
                    P1_DDCConfig = 1;
                    DDCEnable = DDC2;
                    SyncEnable = 0;
                    if (p1) Rate[0] = rx1_rate;
                    Rate[2] = rx1_rate;
                    cntrl1 = RxAdcCtrl1 & 0xff;
                    cntrl2 = RxAdcCtrl2 & 0x3f;
                    if (rx2_enabled)
                    {
                        DDCEnable += DDC3;
                        Rate[3] = rx2_rate;
                    }
                    break;

                case HPSDRModel.REDPITAYA:
                    P1_rxcount = 5;
                    nddc = 5;
                    P1_DDCConfig = 1;
                    DDCEnable = DDC2;
                    SyncEnable = 0;
                    Rate[0] = rx1_rate;
                    Rate[1] = rx1_rate;
                    Rate[2] = rx1_rate;
                    cntrl1 = RxAdcCtrl1 & 0xff;
                    cntrl2 = RxAdcCtrl2 & 0x3f;
                    if (rx2_enabled)
                    {
                        DDCEnable += DDC3;
                        Rate[3] = rx2_rate;
                    }
                    break;

                case HPSDRModel.HERMES:
                case HPSDRModel.ANAN_G2E:
                case HPSDRModel.HERMESLITE:
                case HPSDRModel.ANAN10:
                case HPSDRModel.ANAN100:
                    P1_rxcount = 4;                     // RX4 used for puresignal feedback
                    nddc = 4;
                    P1_DDCConfig = 4;
                    DDCEnable = DDC0;
                    SyncEnable = 0;
                    Rate[0] = rx1_rate;
                    if (rx2_enabled)
                    {
                        DDCEnable += DDC1;
                        Rate[1] = rx2_rate;
                    }
                    break;

                case HPSDRModel.ANAN10E:
                case HPSDRModel.ANAN100B:
                    P1_rxcount = 2;                     // RX2 used for puresignal feedback
                    nddc = 2;
                    P1_DDCConfig = 4;
                    DDCEnable = DDC0;
                    SyncEnable = 0;
                    Rate[0] = rx1_rate;
                    if (rx2_enabled)
                    {
                        DDCEnable += DDC1;
                        Rate[1] = rx2_rate;
                    }
                    break;

                case HPSDRModel.HPSDR:
                    break;
            }

            NetworkIO.EnableRxs(DDCEnable);
            NetworkIO.EnableRxSync(0, SyncEnable);
            for (int i = 0; i < 4; i++)
                NetworkIO.SetDDCRate(i, Rate[i]);
            NetworkIO.SetADC_cntrl1(cntrl1);
            NetworkIO.SetADC_cntrl2(cntrl2);
            NetworkIO.CmdRx();
            NetworkIO.Protocol1DDCConfig(P1_DDCConfig, P1_diversity, P1_rxcount, nddc);
        }

        /// <summary>DDC numbers that carry receiver 1 for this model (the frequency must be sent to each).</summary>
        public static int[] Rx1FrequencyDdcs(HPSDRModel model)
        {
            switch (model)
            {
                case HPSDRModel.HERMES:
                case HPSDRModel.HERMESLITE:
                case HPSDRModel.ANAN10:
                case HPSDRModel.ANAN10E:
                case HPSDRModel.ANAN100:
                case HPSDRModel.ANAN100B:
                case HPSDRModel.ANAN_G2E:
                    return new[] { 0 };
                default:
                    return new[] { 0, 1, 2 };
            }
        }

        public static void UpdateAAudioMixerStates(HPSDRModel model, bool power_on, bool rx2_enabled)
        {
            int RX1 = 1 << WDSP.id(0, 0);
            int RX1S = 1 << WDSP.id(0, 1);
            int RX2 = 1 << WDSP.id(2, 0);
            int MON = 1 << WDSP.id(1, 0);
            int RX2EN = rx2_enabled ? RX2 : 0;

            if (NetworkIO.CurrentRadioProtocol == RadioProtocol.USB)
            {
                // P1: all current models mix both receivers and the TX monitor
                cmaster.SetAAudioMixStates((void*)0, 0, RX1 + RX1S + RX2 + MON, RX1 + RX1S + RX2 + MON);
                cmaster.SetAntiVOXSourceStates(0, RX1 + RX1S + RX2, RX1 + RX1S + RX2);
                return;
            }

            // P2
            if (!power_on)
            {
                cmaster.SetAAudioMixStates((void*)0, 0, RX1 + RX1S + RX2 + MON, 0);
                return;
            }
            cmaster.SetAAudioMixStates((void*)0, 0, RX1 + RX1S + RX2 + MON, RX1 + RX1S + RX2EN + MON);
            switch (model)
            {
                case HPSDRModel.HERMES:
                case HPSDRModel.ANAN_G2E:
                case HPSDRModel.HERMESLITE:
                case HPSDRModel.ANAN10E:
                case HPSDRModel.ANAN10:
                case HPSDRModel.ANAN100B:
                case HPSDRModel.ANAN100:
                    cmaster.SetAntiVOXSourceStates(0, RX1 + RX1S + RX2, RX1 + RX1S + RX2EN);
                    break;
            }
        }
    }
}
