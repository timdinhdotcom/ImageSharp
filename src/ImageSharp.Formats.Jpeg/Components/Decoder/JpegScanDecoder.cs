﻿// <copyright file="JpegScanDecoder.cs" company="James Jackson-South">
// Copyright (c) James Jackson-South and contributors.
// Licensed under the Apache License, Version 2.0.
// </copyright>

// ReSharper disable InconsistentNaming
namespace ImageSharp.Formats.Jpg
{
    using System;
    using System.Runtime.CompilerServices;

    /// <summary>
    /// Encapsulates the impementation of Jpeg SOS decoder.
    /// See JpegScanDecoder.md!
    /// </summary>
    internal unsafe partial struct JpegScanDecoder
    {
        /// <summary>
        /// The AC table index
        /// </summary>
        private const int AcTableIndex = 1;

        /// <summary>
        /// The DC table index
        /// </summary>
        private const int DcTableIndex = 0;

        /// <summary>
        /// X coordinate of the current block, in units of 8x8. (The third block in the first row has (bx, by) = (2, 0))
        /// </summary>
        private int bx;

        /// <summary>
        /// Y coordinate of the current block, in units of 8x8. (The third block in the first row has (bx, by) = (2, 0))
        /// </summary>
        private int by;

        // zigStart and zigEnd are the spectral selection bounds.
        // ah and al are the successive approximation high and low values.
        // The spec calls these values Ss, Se, Ah and Al.
        // For progressive JPEGs, these are the two more-or-less independent
        // aspects of progression. Spectral selection progression is when not
        // all of a block's 64 DCT coefficients are transmitted in one pass.
        // For example, three passes could transmit coefficient 0 (the DC
        // component), coefficients 1-5, and coefficients 6-63, in zig-zag
        // order. Successive approximation is when not all of the bits of a
        // band of coefficients are transmitted in one pass. For example,
        // three passes could transmit the 6 most significant bits, followed
        // by the second-least significant bit, followed by the least
        // significant bit.
        // For baseline JPEGs, these parameters are hard-coded to 0/63/0/0.

        /// <summary>
        /// Start index of the zig-zag selection bound
        /// </summary>
        private int zigStart;

        /// <summary>
        /// End index of the zig-zag selection bound
        /// </summary>
        private int zigEnd;

        /// <summary>
        /// Successive approximation high value
        /// </summary>
        private int ah;

        /// <summary>
        /// Successive approximation low value
        /// </summary>
        private int al;

        /// <summary>
        /// The number of component scans
        /// </summary>
        private int componentScanCount;

        /// <summary>
        /// The current component index
        /// </summary>
        private int componentIndex;

        /// <summary>
        /// Horizontal sampling factor at the current component index
        /// </summary>
        private int hi;

        /// <summary>
        /// End-of-Band run, specified in section G.1.2.2.
        /// </summary>
        private ushort eobRun;

        /// <summary>
        /// The <see cref="ComputationData"/> buffer
        /// </summary>
        private ComputationData data;

        /// <summary>
        /// Pointers to elements of <see cref="data"/>
        /// </summary>
        private DataPointers pointers;

        /// <summary>
        /// Initializes the default instance after creation.
        /// </summary>
        /// <param name="p">Pointer to <see cref="JpegScanDecoder"/> on the stack</param>
        /// <param name="decoder">The <see cref="JpegDecoderCore"/> instance</param>
        /// <param name="remaining">The remaining bytes in the segment block.</param>
        public static void Init(JpegScanDecoder* p, JpegDecoderCore decoder, int remaining)
        {
            p->data = ComputationData.Create();
            p->pointers = new DataPointers(&p->data);
            p->InitImpl(decoder, remaining);
        }

        /// <summary>
        /// Reads the blocks from the <see cref="JpegDecoderCore"/>-s stream, and processes them into the corresponding <see cref="JpegPixelArea"/> instances.
        /// </summary>
        /// <param name="decoder">The <see cref="JpegDecoderCore"/> instance</param>
        public void ProcessBlocks(JpegDecoderCore decoder)
        {
            int blockCount = 0;
            int mcu = 0;
            byte expectedRst = JpegConstants.Markers.RST0;

            for (int my = 0; my < decoder.MCUCountY; my++)
            {
                for (int mx = 0; mx < decoder.MCUCountX; mx++)
                {
                    for (int i = 0; i < this.componentScanCount; i++)
                    {
                        this.componentIndex = this.pointers.ComponentScan[i].ComponentIndex;
                        this.hi = decoder.ComponentArray[this.componentIndex].HorizontalFactor;
                        int vi = decoder.ComponentArray[this.componentIndex].VerticalFactor;

                        for (int j = 0; j < this.hi * vi; j++)
                        {
                            // The blocks are traversed one MCU at a time. For 4:2:0 chroma
                            // subsampling, there are four Y 8x8 blocks in every 16x16 MCU.
                            // For a baseline 32x16 pixel image, the Y blocks visiting order is:
                            // 0 1 4 5
                            // 2 3 6 7
                            // For progressive images, the interleaved scans (those with component count > 1)
                            // are traversed as above, but non-interleaved scans are traversed left
                            // to right, top to bottom:
                            // 0 1 2 3
                            // 4 5 6 7
                            // Only DC scans (zigStart == 0) can be interleave AC scans must have
                            // only one component.
                            // To further complicate matters, for non-interleaved scans, there is no
                            // data for any blocks that are inside the image at the MCU level but
                            // outside the image at the pixel level. For example, a 24x16 pixel 4:2:0
                            // progressive image consists of two 16x16 MCUs. The interleaved scans
                            // will process 8 Y blocks:
                            // 0 1 4 5
                            // 2 3 6 7
                            // The non-interleaved scans will process only 6 Y blocks:
                            // 0 1 2
                            // 3 4 5
                            if (this.componentScanCount != 1)
                            {
                                this.bx = (this.hi * mx) + (j % this.hi);
                                this.by = (vi * my) + (j / this.hi);
                            }
                            else
                            {
                                int q = decoder.MCUCountX * this.hi;
                                this.bx = blockCount % q;
                                this.by = blockCount / q;
                                blockCount++;
                                if (this.bx * 8 >= decoder.ImageWidth || this.by * 8 >= decoder.ImageHeight)
                                {
                                    continue;
                                }
                            }

                            int qtIndex = decoder.ComponentArray[this.componentIndex].Selector;

                            // TODO: Reading & processing blocks should be done in 2 separate loops. The second one could be parallelized. The first one could be async.
                            this.data.QuantiazationTable = decoder.QuantizationTables[qtIndex];

                            // Load the previous partially decoded coefficients, if applicable.
                            if (decoder.IsProgressive)
                            {
                                int blockIndex = this.GetBlockIndex(decoder);
                                this.data.Block = decoder.DecodedBlocks[this.componentIndex][blockIndex];
                            }
                            else
                            {
                                this.data.Block.Clear();
                            }

                            this.ProcessBlockImpl(decoder, i);
                        }

                        // for j
                    }

                    // for i
                    mcu++;

                    if (decoder.RestartInterval > 0 && mcu % decoder.RestartInterval == 0 && mcu < decoder.TotalMCUCount)
                    {
                        // A more sophisticated decoder could use RST[0-7] markers to resynchronize from corrupt input,
                        // but this one assumes well-formed input, and hence the restart marker follows immediately.
                        decoder.ReadFull(decoder.Temp, 0, 2);
                        if (decoder.Temp[0] != 0xff || decoder.Temp[1] != expectedRst)
                        {
                            throw new ImageFormatException("Bad RST marker");
                        }

                        expectedRst++;
                        if (expectedRst == JpegConstants.Markers.RST7 + 1)
                        {
                            expectedRst = JpegConstants.Markers.RST0;
                        }

                        // Reset the Huffman decoder.
                        decoder.Bits = default(Bits);

                        // Reset the DC components, as per section F.2.1.3.1.
                        this.ResetDc();

                        // Reset the progressive decoder state, as per section G.1.2.2.
                        this.eobRun = 0;
                    }
                }

                // for mx
            }
        }

        private void ResetDc()
        {
            Unsafe.InitBlock(this.pointers.Dc, default(byte), sizeof(int) * JpegDecoderCore.MaxComponents);
        }

        /// <summary>
        /// The implementation part of <see cref="Init"/> as an instance method.
        /// </summary>
        /// <param name="decoder">The <see cref="JpegDecoderCore"/></param>
        /// <param name="remaining">The remaining bytes</param>
        private void InitImpl(JpegDecoderCore decoder, int remaining)
        {
            if (decoder.ComponentCount == 0)
            {
                throw new ImageFormatException("Missing SOF marker");
            }

            if (remaining < 6 || 4 + (2 * decoder.ComponentCount) < remaining || remaining % 2 != 0)
            {
                throw new ImageFormatException("SOS has wrong length");
            }

            decoder.ReadFull(decoder.Temp, 0, remaining);
            this.componentScanCount = decoder.Temp[0];

            int scanComponentCountX2 = 2 * this.componentScanCount;
            if (remaining != 4 + scanComponentCountX2)
            {
                throw new ImageFormatException("SOS length inconsistent with number of components");
            }

            int totalHv = 0;

            for (int i = 0; i < this.componentScanCount; i++)
            {
                this.ProcessScanImpl(decoder, i, ref this.pointers.ComponentScan[i], ref totalHv);
            }

            // Section B.2.3 states that if there is more than one component then the
            // total H*V values in a scan must be <= 10.
            if (decoder.ComponentCount > 1 && totalHv > 10)
            {
                throw new ImageFormatException("Total sampling factors too large.");
            }

            this.zigEnd = Block8x8F.ScalarCount - 1;

            if (decoder.IsProgressive)
            {
                this.zigStart = decoder.Temp[1 + scanComponentCountX2];
                this.zigEnd = decoder.Temp[2 + scanComponentCountX2];
                this.ah = decoder.Temp[3 + scanComponentCountX2] >> 4;
                this.al = decoder.Temp[3 + scanComponentCountX2] & 0x0f;

                if ((this.zigStart == 0 && this.zigEnd != 0) || this.zigStart > this.zigEnd
                    || this.zigEnd >= Block8x8F.ScalarCount)
                {
                    throw new ImageFormatException("Bad spectral selection bounds");
                }

                if (this.zigStart != 0 && this.componentScanCount != 1)
                {
                    throw new ImageFormatException("Progressive AC coefficients for more than one component");
                }

                if (this.ah != 0 && this.ah != this.al + 1)
                {
                    throw new ImageFormatException("Bad successive approximation values");
                }
            }
        }

        /// <summary>
        /// Process the current block at (<see cref="bx"/>, <see cref="by"/>)
        /// </summary>
        /// <param name="decoder">The decoder</param>
        /// <param name="i">The index of the scan</param>
        private void ProcessBlockImpl(JpegDecoderCore decoder, int i)
        {
            var b = this.pointers.Block;

            int huffmannIdx = (AcTableIndex * HuffmanTree.ThRowSize) + this.pointers.ComponentScan[i].AcTableSelector;
            if (this.ah != 0)
            {
                this.Refine(decoder, ref decoder.HuffmanTrees[huffmannIdx], 1 << this.al);
            }
            else
            {
                int zig = this.zigStart;
                if (zig == 0)
                {
                    zig++;

                    // Decode the DC coefficient, as specified in section F.2.2.1.
                    byte value =
                        decoder.DecodeHuffman(
                            ref decoder.HuffmanTrees[(DcTableIndex * HuffmanTree.ThRowSize) + this.pointers.ComponentScan[i].DcTableSelector]);
                    if (value > 16)
                    {
                        throw new ImageFormatException("Excessive DC component");
                    }

                    int deltaDC = decoder.Bits.ReceiveExtend(value, decoder);
                    this.pointers.Dc[this.componentIndex] += deltaDC;

                    // b[0] = dc[compIndex] << al;
                    Block8x8F.SetScalarAt(b, 0, this.pointers.Dc[this.componentIndex] << this.al);
                }

                if (zig <= this.zigEnd && this.eobRun > 0)
                {
                    this.eobRun--;
                }
                else
                {
                    // Decode the AC coefficients, as specified in section F.2.2.2.
                    for (; zig <= this.zigEnd; zig++)
                    {
                        byte value = decoder.DecodeHuffman(ref decoder.HuffmanTrees[huffmannIdx]);
                        byte val0 = (byte)(value >> 4);
                        byte val1 = (byte)(value & 0x0f);
                        if (val1 != 0)
                        {
                            zig += val0;
                            if (zig > this.zigEnd)
                            {
                                break;
                            }

                            int ac = decoder.Bits.ReceiveExtend(val1, decoder);

                            // b[Unzig[zig]] = ac << al;
                            Block8x8F.SetScalarAt(b, this.pointers.Unzig[zig], ac << this.al);
                        }
                        else
                        {
                            if (val0 != 0x0f)
                            {
                                this.eobRun = (ushort)(1 << val0);
                                if (val0 != 0)
                                {
                                    this.eobRun |= (ushort)decoder.DecodeBits(val0);
                                }

                                this.eobRun--;
                                break;
                            }

                            zig += 0x0f;
                        }
                    }
                }
            }

            if (decoder.IsProgressive)
            {
                if (this.zigEnd != Block8x8F.ScalarCount - 1 || this.al != 0)
                {
                    // We haven't completely decoded this 8x8 block. Save the coefficients.
                    decoder.DecodedBlocks[this.componentIndex][this.GetBlockIndex(decoder)] = *b;

                    // At this point, we could execute the rest of the loop body to dequantize and
                    // perform the inverse DCT, to save early stages of a progressive image to the
                    // *image.YCbCr buffers (the whole point of progressive encoding), but in Go,
                    // the jpeg.Decode function does not return until the entire image is decoded,
                    // so we "continue" here to avoid wasted computation.
                    return;
                }
            }

            // Dequantize, perform the inverse DCT and store the block to the image.
            Block8x8F.UnZig(b, this.pointers.QuantiazationTable, this.pointers.Unzig);

            DCT.TransformIDCT(ref *b, ref *this.pointers.Temp1, ref *this.pointers.Temp2);

            var destChannel = decoder.GetDestinationChannel(this.componentIndex);
            var destArea = destChannel.GetOffsetedSubAreaForBlock(this.bx, this.by);
            destArea.LoadColorsFrom(this.pointers.Temp1, this.pointers.Temp2);
        }

        /// <summary>
        /// Gets the block index used to retieve blocks from in <see cref="JpegDecoderCore.DecodedBlocks"/>
        /// </summary>
        /// <param name="decoder">The <see cref="JpegDecoderCore"/> instance</param>
        /// <returns>The index</returns>
        private int GetBlockIndex(JpegDecoderCore decoder)
        {
            return ((this.@by * decoder.MCUCountX) * this.hi) + this.bx;
        }

        private void ProcessScanImpl(JpegDecoderCore decoder, int i, ref ComponentScan currentComponentScan, ref int totalHv)
        {
            // Component selector.
            int cs = decoder.Temp[1 + (2 * i)];
            int compIndex = -1;
            for (int j = 0; j < decoder.ComponentCount; j++)
            {
                // Component compv = ;
                if (cs == decoder.ComponentArray[j].Identifier)
                {
                    compIndex = j;
                }
            }

            if (compIndex < 0)
            {
                throw new ImageFormatException("Unknown component selector");
            }

            currentComponentScan.ComponentIndex = (byte)compIndex;

            this.ProcessComponentImpl(decoder, i, ref currentComponentScan, ref totalHv, ref decoder.ComponentArray[compIndex]);
        }

        private void ProcessComponentImpl(
            JpegDecoderCore decoder,
            int i,
            ref ComponentScan currentComponentScan,
            ref int totalHv,
            ref Component currentComponent)
        {
            // Section B.2.3 states that "the value of Cs_j shall be different from
            // the values of Cs_1 through Cs_(j-1)". Since we have previously
            // verified that a frame's component identifiers (C_i values in section
            // B.2.2) are unique, it suffices to check that the implicit indexes
            // into comp are unique.
            for (int j = 0; j < i; j++)
            {
                if (currentComponentScan.ComponentIndex == this.pointers.ComponentScan[j].ComponentIndex)
                {
                    throw new ImageFormatException("Repeated component selector");
                }
            }

            totalHv += currentComponent.HorizontalFactor * currentComponent.VerticalFactor;

            currentComponentScan.DcTableSelector = (byte)(decoder.Temp[2 + (2 * i)] >> 4);
            if (currentComponentScan.DcTableSelector > HuffmanTree.MaxTh)
            {
                throw new ImageFormatException("Bad DC table selector value");
            }

            currentComponentScan.AcTableSelector = (byte)(decoder.Temp[2 + (2 * i)] & 0x0f);
            if (currentComponentScan.AcTableSelector > HuffmanTree.MaxTh)
            {
                throw new ImageFormatException("Bad AC table selector  value");
            }
        }

        /// <summary>
        /// Decodes a successive approximation refinement block, as specified in section G.1.2.
        /// </summary>
        /// <param name="decoder">The decoder instance</param>
        /// <param name="h">The Huffman tree</param>
        /// <param name="delta">The low transform offset</param>
        private void Refine(JpegDecoderCore decoder, ref HuffmanTree h, int delta)
        {
            Block8x8F* b = this.pointers.Block;

            // Refining a DC component is trivial.
            if (this.zigStart == 0)
            {
                if (this.zigEnd != 0)
                {
                    throw new ImageFormatException("Invalid state for zig DC component");
                }

                bool bit = decoder.DecodeBit();
                if (bit)
                {
                    int stuff = (int)Block8x8F.GetScalarAt(b, 0);

                    // int stuff = (int)b[0];
                    stuff |= delta;

                    // b[0] = stuff;
                    Block8x8F.SetScalarAt(b, 0, stuff);
                }

                return;
            }

            // Refining AC components is more complicated; see sections G.1.2.2 and G.1.2.3.
            int zig = this.zigStart;
            if (this.eobRun == 0)
            {
                for (; zig <= this.zigEnd; zig++)
                {
                    bool done = false;
                    int z = 0;
                    byte val = decoder.DecodeHuffman(ref h);
                    int val0 = val >> 4;
                    int val1 = val & 0x0f;

                    switch (val1)
                    {
                        case 0:
                            if (val0 != 0x0f)
                            {
                                this.eobRun = (ushort)(1 << val0);
                                if (val0 != 0)
                                {
                                    this.eobRun |= (ushort)decoder.DecodeBits(val0);
                                }

                                done = true;
                            }

                            break;
                        case 1:
                            z = delta;
                            bool bit = decoder.DecodeBit();
                            if (!bit)
                            {
                                z = -z;
                            }

                            break;
                        default:
                            throw new ImageFormatException("Unexpected Huffman code");
                    }

                    if (done)
                    {
                        break;
                    }

                    zig = this.RefineNonZeroes(decoder, zig, val0, delta);
                    if (zig > this.zigEnd)
                    {
                        throw new ImageFormatException($"Too many coefficients {zig} > {this.zigEnd}");
                    }

                    if (z != 0)
                    {
                        // b[Unzig[zig]] = z;
                        Block8x8F.SetScalarAt(b, this.pointers.Unzig[zig], z);
                    }
                }
            }

            if (this.eobRun > 0)
            {
                this.eobRun--;
                this.RefineNonZeroes(decoder, zig, -1, delta);
            }
        }

        /// <summary>
        /// Refines non-zero entries of b in zig-zag order.
        /// If <paramref name="nz" /> >= 0, the first <paramref name="nz" /> zero entries are skipped over.
        /// </summary>
        /// <param name="decoder">The decoder</param>
        /// <param name="zig">The zig-zag start index</param>
        /// <param name="nz">The non-zero entry</param>
        /// <param name="delta">The low transform offset</param>
        /// <returns>The <see cref="int" /></returns>
        private int RefineNonZeroes(JpegDecoderCore decoder, int zig, int nz, int delta)
        {
            var b = this.pointers.Block;
            for (; zig <= this.zigEnd; zig++)
            {
                int u = this.pointers.Unzig[zig];
                float bu = Block8x8F.GetScalarAt(b, u);

                // TODO: Are the equality comparsions OK with floating point values? Isn't an epsilon value necessary?
                if (bu == 0)
                {
                    if (nz == 0)
                    {
                        break;
                    }

                    nz--;
                    continue;
                }

                bool bit = decoder.DecodeBit();
                if (!bit)
                {
                    continue;
                }

                if (bu >= 0)
                {
                    // b[u] += delta;
                    Block8x8F.SetScalarAt(b, u, bu + delta);
                }
                else
                {
                    // b[u] -= delta;
                    Block8x8F.SetScalarAt(b, u, bu - delta);
                }
            }

            return zig;
        }
    }
}