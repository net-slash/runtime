// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static Interop.Crypt32;
using CryptProvParam = Interop.Advapi32.CryptProvParam;
using X509IssuerSerial = System.Security.Cryptography.Xml.X509IssuerSerial;

namespace Internal.Cryptography.Pal.Windows
{
    internal static class HelpersWindows
    {
        public static CryptographicException ToCryptographicException(this ErrorCode errorCode)
        {
            return ((int)errorCode).ToCryptographicException();
        }

        public static string ToStringAnsi(this IntPtr psz)
        {
            return Marshal.PtrToStringAnsi(psz)!;
        }

        // Used for binary blobs without internal pointers.
        public static unsafe byte[] GetMsgParamAsByteArray(this SafeCryptMsgHandle hCryptMsg, CryptMsgParamType paramType, int index = 0)
        {
            int cbData = 0;
            if (!Interop.Crypt32.CryptMsgGetParam(hCryptMsg, paramType, index, IntPtr.Zero, ref cbData))
                throw Marshal.GetLastPInvokeError().ToCryptographicException();

            byte[] data = new byte[cbData];
            fixed (byte* pvData = data)
            {
                if (!Interop.Crypt32.CryptMsgGetParam(hCryptMsg, paramType, index, pvData, ref cbData))
                    throw Marshal.GetLastPInvokeError().ToCryptographicException();
            }

            return data.Resize(cbData);
        }

        // Used for binary blobs with internal pointers.
        public static SafeHandle GetMsgParamAsMemory(this SafeCryptMsgHandle hCryptMsg, CryptMsgParamType paramType, int index = 0)
        {
            int cbData = 0;
            if (!Interop.Crypt32.CryptMsgGetParam(hCryptMsg, paramType, index, IntPtr.Zero, ref cbData))
            {
                throw Marshal.GetLastPInvokeError().ToCryptographicException();
            }

            SafeHandle pvData = SafeHeapAllocHandle.Alloc(cbData);
            if (!Interop.Crypt32.CryptMsgGetParam(hCryptMsg, paramType, index, pvData.DangerousGetHandle(), ref cbData))
            {
                Exception e = Marshal.GetLastPInvokeError().ToCryptographicException();
                pvData.Dispose();
                throw e;
            }

            return pvData;
        }

        public static byte[] ToByteArray(this DATA_BLOB blob)
        {
            if (blob.cbData == 0)
                return Array.Empty<byte>();

            int length = (int)(blob.cbData);
            byte[] data = new byte[length];
            Marshal.Copy(blob.pbData, data, 0, length);
            return data;
        }

        public static CryptMsgType GetMessageType(this SafeCryptMsgHandle hCryptMsg)
        {
            int cbData = sizeof(CryptMsgType);
            CryptMsgType cryptMsgType;
            if (!Interop.Crypt32.CryptMsgGetParam(hCryptMsg, CryptMsgParamType.CMSG_TYPE_PARAM, 0, out cryptMsgType, ref cbData))
                throw Marshal.GetLastPInvokeError().ToCryptographicException();
            return cryptMsgType;
        }

        public static int GetVersion(this SafeCryptMsgHandle hCryptMsg)
        {
            int cbData = sizeof(int);
            int version;
            if (!Interop.Crypt32.CryptMsgGetParam(hCryptMsg, CryptMsgParamType.CMSG_VERSION_PARAM, 0, out version, ref cbData))
                throw Marshal.GetLastPInvokeError().ToCryptographicException();
            return version;
        }

        /// <summary>
        /// Returns the inner content of the CMS.
        ///
        /// Special case: If the CMS is an enveloped CMS that has been decrypted and the inner content type is Oids.Pkcs7Data, the returned
        /// content bytes are the decoded octet bytes, rather than the encoding of those bytes. This is a documented convenience behavior of
        /// CryptMsgGetParam(CMSG_CONTENT_PARAM) that apparently got baked into the behavior of the managed EnvelopedCms class.
        /// </summary>
        public static ContentInfo GetContentInfo(this SafeCryptMsgHandle hCryptMsg)
        {
            byte[] oidBytes = hCryptMsg.GetMsgParamAsByteArray(CryptMsgParamType.CMSG_INNER_CONTENT_TYPE_PARAM);

            // .NET Framework compat: If we get a null or non-terminated string back from Crypt32, throwing an exception seems more apropros but
            // for the .NET Framework compat, we throw the result at the ASCII Encoder and let the chips fall where they may.
            int length = oidBytes.Length;
            if (length > 0 && oidBytes[length - 1] == 0)
            {
                length--;
            }

            string oidValue = Encoding.ASCII.GetString(oidBytes, 0, length);
            Oid contentType = new Oid(oidValue);
            byte[] content = hCryptMsg.GetMsgParamAsByteArray(CryptMsgParamType.CMSG_CONTENT_PARAM);

            return new ContentInfo(contentType, content);
        }

        public static X509Certificate2Collection GetOriginatorCerts(this SafeCryptMsgHandle hCryptMsg)
        {
            int numCertificates;
            int cbNumCertificates = sizeof(int);
            if (!Interop.Crypt32.CryptMsgGetParam(hCryptMsg, CryptMsgParamType.CMSG_CERT_COUNT_PARAM, 0, out numCertificates, ref cbNumCertificates))
                throw Marshal.GetLastPInvokeError().ToCryptographicException();
            X509Certificate2Collection certs = new X509Certificate2Collection();
            for (int index = 0; index < numCertificates; index++)
            {
                byte[] encodedCertificate = hCryptMsg.GetMsgParamAsByteArray(CryptMsgParamType.CMSG_CERT_PARAM, index);
                X509Certificate2 cert = X509CertificateLoader.LoadCertificate(encodedCertificate);
                certs.Add(cert);
            }
            return certs;
        }

        /// <summary>
        /// Returns (AlgId)(-1) if oid is unknown.
        /// </summary>
        public static AlgId ToAlgId(this string oidValue)
        {
            CRYPT_OID_INFO info = Interop.Crypt32.FindOidInfo(CryptOidInfoKeyType.CRYPT_OID_INFO_OID_KEY, oidValue, OidGroup.All, false);
            return (AlgId)(info.AlgId);
        }

        public static SafeCertContextHandle CreateCertContextHandle(this X509Certificate2 cert)
        {
            IntPtr pCertContext = cert.Handle;
            pCertContext = Interop.Crypt32.CertDuplicateCertificateContext(pCertContext);
            SafeCertContextHandle hCertContext = new SafeCertContextHandle(pCertContext);
            GC.KeepAlive(cert);
            return hCertContext;
        }

        public static byte[] GetSubjectKeyIdentifier(this SafeCertContextHandle hCertContext)
        {
            int cbData = 0;
            if (!Interop.Crypt32.CertGetCertificateContextProperty(hCertContext, CertContextPropId.CERT_KEY_IDENTIFIER_PROP_ID, null, ref cbData))
                throw Marshal.GetLastPInvokeError().ToCryptographicException();

            byte[] ski = new byte[cbData];
            if (!Interop.Crypt32.CertGetCertificateContextProperty(hCertContext, CertContextPropId.CERT_KEY_IDENTIFIER_PROP_ID, ski, ref cbData))
                throw Marshal.GetLastPInvokeError().ToCryptographicException();

            return ski.Resize(cbData);
        }

        public static SubjectIdentifier ToSubjectIdentifier(this CERT_ID certId)
        {
            switch (certId.dwIdChoice)
            {
                case CertIdChoice.CERT_ID_ISSUER_SERIAL_NUMBER:
                    {
                        const int dwStrType = (int)(CertNameStrTypeAndFlags.CERT_X500_NAME_STR | CertNameStrTypeAndFlags.CERT_NAME_STR_REVERSE_FLAG);

                        string issuer;
                        unsafe
                        {
                            DATA_BLOB* dataBlobPtr = &certId.u.IssuerSerialNumber.Issuer;

                            int nc = Interop.Crypt32.CertNameToStr((int)MsgEncodingType.All, dataBlobPtr, dwStrType, null, 0);
                            if (nc <= 1) // The API actually return 1 when it fails; which is not what the documentation says.
                            {
                                throw Marshal.GetLastPInvokeError().ToCryptographicException();
                            }

                            Span<char> name = nc <= 128 ? stackalloc char[128] : new char[nc];
                            fixed (char* namePtr = name)
                            {
                                nc = Interop.Crypt32.CertNameToStr((int)MsgEncodingType.All, dataBlobPtr, dwStrType, namePtr, nc);
                                if (nc <= 1) // The API actually return 1 when it fails; which is not what the documentation says.
                                {
                                    throw Marshal.GetLastPInvokeError().ToCryptographicException();
                                }

                                issuer = new string(namePtr);
                            }
                        }

                        byte[] serial = certId.u.IssuerSerialNumber.SerialNumber.ToByteArray();
                        X509IssuerSerial issuerSerial = new X509IssuerSerial(issuer, serial.ToSerialString());
                        return new SubjectIdentifier(SubjectIdentifierType.IssuerAndSerialNumber, issuerSerial);
                    }

                case CertIdChoice.CERT_ID_KEY_IDENTIFIER:
                    {
                        byte[] ski = certId.u.KeyId.ToByteArray();
                        return new SubjectIdentifier(SubjectIdentifierType.SubjectKeyIdentifier, ski.ToSkiString());
                    }

                default:
                    throw new CryptographicException(SR.Format(SR.Cryptography_Cms_Invalid_Subject_Identifier_Type, certId.dwIdChoice));
            }
        }

        public static SubjectIdentifierOrKey ToSubjectIdentifierOrKey(this CERT_ID certId)
        {
            //
            // SubjectIdentifierOrKey is just a SubjectIdentifier with an (irrelevant here) "key" option thumbtacked onto it so
            // the easiest way is to subcontract the job to SubjectIdentifier.
            //
            SubjectIdentifier subjectIdentifier = certId.ToSubjectIdentifier();
            SubjectIdentifierType subjectIdentifierType = subjectIdentifier.Type;
            switch (subjectIdentifierType)
            {
                case SubjectIdentifierType.IssuerAndSerialNumber:
                    return new SubjectIdentifierOrKey(SubjectIdentifierOrKeyType.IssuerAndSerialNumber, subjectIdentifier.Value!);

                case SubjectIdentifierType.SubjectKeyIdentifier:
                    return new SubjectIdentifierOrKey(SubjectIdentifierOrKeyType.SubjectKeyIdentifier, subjectIdentifier.Value!);

                default:
                    Debug.Fail("Only the framework can construct SubjectIdentifier's so if we got a bad value here, that's our fault.");
                    throw new CryptographicException(SR.Format(SR.Cryptography_Cms_Invalid_Subject_Identifier_Type, subjectIdentifierType));
            }
        }

        public static SubjectIdentifierOrKey ToSubjectIdentifierOrKey(this CERT_PUBLIC_KEY_INFO publicKeyInfo)
        {
            int keyLength = Interop.Crypt32.CertGetPublicKeyLength(MsgEncodingType.All, ref publicKeyInfo);
            string oidValue = publicKeyInfo.Algorithm.pszObjId.ToStringAnsi();
            AlgorithmIdentifier algorithmId = new AlgorithmIdentifier(Oid.FromOidValue(oidValue, OidGroup.PublicKeyAlgorithm), keyLength);

            byte[] keyValue = publicKeyInfo.PublicKey.ToByteArray();
            PublicKeyInfo pki = new PublicKeyInfo(algorithmId, keyValue);
            return new SubjectIdentifierOrKey(SubjectIdentifierOrKeyType.PublicKeyInfo, pki);
        }

        public static AlgorithmIdentifier ToAlgorithmIdentifier(this CRYPT_ALGORITHM_IDENTIFIER cryptAlgorithmIdentifier)
        {
            string oidValue = cryptAlgorithmIdentifier.pszObjId.ToStringAnsi();
            AlgId algId = oidValue.ToAlgId();

            int keyLength;
            switch (algId)
            {
                case AlgId.CALG_RC2:
                    {
                        if (cryptAlgorithmIdentifier.Parameters.cbData == 0)
                        {
                            keyLength = 0;
                        }
                        else
                        {
                            CRYPT_RC2_CBC_PARAMETERS rc2Parameters;
                            unsafe
                            {
                                int cbSize = sizeof(CRYPT_RC2_CBC_PARAMETERS);
                                if (!Interop.Crypt32.CryptDecodeObject(CryptDecodeObjectStructType.PKCS_RC2_CBC_PARAMETERS, cryptAlgorithmIdentifier.Parameters.pbData, (int)(cryptAlgorithmIdentifier.Parameters.cbData), &rc2Parameters, ref cbSize))
                                    throw Marshal.GetLastPInvokeError().ToCryptographicException();
                            }

                            keyLength = rc2Parameters.dwVersion switch
                            {
                                CryptRc2Version.CRYPT_RC2_40BIT_VERSION => KeyLengths.Rc2_40Bit,
                                CryptRc2Version.CRYPT_RC2_56BIT_VERSION => KeyLengths.Rc2_56Bit,
                                CryptRc2Version.CRYPT_RC2_64BIT_VERSION => KeyLengths.Rc2_64Bit,
                                CryptRc2Version.CRYPT_RC2_128BIT_VERSION => KeyLengths.Rc2_128Bit,
                                _ => 0,
                            };
                        }
                        break;
                    }

                case AlgId.CALG_RC4:
                    {
                        int saltLength = 0;
                        if (cryptAlgorithmIdentifier.Parameters.cbData != 0)
                        {
                            using (SafeHandle sh = Interop.Crypt32.CryptDecodeObjectToMemory(CryptDecodeObjectStructType.X509_OCTET_STRING, cryptAlgorithmIdentifier.Parameters.pbData, (int)cryptAlgorithmIdentifier.Parameters.cbData))
                            {
                                unsafe
                                {
                                    DATA_BLOB* pDataBlob = (DATA_BLOB*)(sh.DangerousGetHandle());
                                    saltLength = (int)(pDataBlob->cbData);
                                }
                            }
                        }

                        // For RC4, keyLength = 128 - (salt length * 8).
                        keyLength = KeyLengths.Rc4Max_128Bit - saltLength * 8;
                        break;
                    }

                case AlgId.CALG_DES:
                    // DES key length is fixed at 64 (or 56 without the parity bits).
                    keyLength = KeyLengths.Des_64Bit;
                    break;

                case AlgId.CALG_3DES:
                    // 3DES key length is fixed at 192 (or 168 without the parity bits).
                    keyLength = KeyLengths.TripleDes_192Bit;
                    break;

                default:
                    // We've exhausted all the algorithm types that the .NET Framework used to set the KeyLength for. Key lengths are not a viable way of
                    // identifying algorithms in the long run so we will not extend this list any further.
                    keyLength = 0;
                    break;
            }

            AlgorithmIdentifier algorithmIdentifier = new AlgorithmIdentifier(Oid.FromOidValue(oidValue, OidGroup.All), keyLength);
            switch (oidValue)
            {
                case Oids.RsaOaep:
                    algorithmIdentifier.Parameters = cryptAlgorithmIdentifier.Parameters.ToByteArray();
                    break;
            }
            return algorithmIdentifier;
        }

        public static CryptographicAttributeObjectCollection GetUnprotectedAttributes(this SafeCryptMsgHandle hCryptMsg)
        {
            // For some reason, you can't ask how many attributes there are - you have to ask for the attributes and
            // get a CRYPT_E_ATTRIBUTES_MISSING failure if the count is 0.
            int cbUnprotectedAttr = 0;
            if (!Interop.Crypt32.CryptMsgGetParam(hCryptMsg, CryptMsgParamType.CMSG_UNPROTECTED_ATTR_PARAM, 0, IntPtr.Zero, ref cbUnprotectedAttr))
            {
                int lastError = Marshal.GetLastPInvokeError();
                if (lastError == (int)ErrorCode.CRYPT_E_ATTRIBUTES_MISSING)
                    return new CryptographicAttributeObjectCollection();
                throw lastError.ToCryptographicException();
            }

            using (SafeHandle sh = hCryptMsg.GetMsgParamAsMemory(CryptMsgParamType.CMSG_UNPROTECTED_ATTR_PARAM))
            {
                unsafe
                {
                    CRYPT_ATTRIBUTES* pCryptAttributes = (CRYPT_ATTRIBUTES*)(sh.DangerousGetHandle());
                    return ToCryptographicAttributeObjectCollection(pCryptAttributes);
                }
            }
        }

        public static CspParameters GetProvParameters(this SafeProvOrNCryptKeyHandle handle)
        {
            // A normal key container name is a GUID (~34 bytes ASCII)
            // The longest standard provider name is 64 bytes (including the \0),
            // but we shouldn't have a CAPI call with a software CSP.
            //
            // In debug builds use a buffer which will need to be resized, but is big
            // enough to hold the DWORD "can't fail" values.
            Span<byte> stackSpan = stackalloc byte[
#if DEBUG
                sizeof(int)
#else
                64
#endif
                ];

            stackSpan.Clear();
            int size = stackSpan.Length;

            if (!Interop.Advapi32.CryptGetProvParam(handle, CryptProvParam.PP_PROVTYPE, stackSpan, ref size))
            {
                throw Marshal.GetLastPInvokeError().ToCryptographicException();
            }

            if (size != sizeof(int))
            {
                Debug.Fail("PP_PROVTYPE writes a DWORD - enum misalignment?");
                throw new CryptographicException();
            }

            int provType = MemoryMarshal.Read<int>(stackSpan.Slice(0, size));

            size = stackSpan.Length;
            if (!Interop.Advapi32.CryptGetProvParam(handle, CryptProvParam.PP_KEYSET_TYPE, stackSpan, ref size))
            {
                throw Marshal.GetLastPInvokeError().ToCryptographicException();
            }

            if (size != sizeof(int))
            {
                Debug.Fail("PP_KEYSET_TYPE writes a DWORD - enum misalignment?");
                throw new CryptographicException();
            }

            int keysetType = MemoryMarshal.Read<int>(stackSpan.Slice(0, size));

            // Only CRYPT_MACHINE_KEYSET is described as coming back, but be defensive.
            CspProviderFlags provFlags =
                ((CspProviderFlags)keysetType & CspProviderFlags.UseMachineKeyStore) |
                CspProviderFlags.UseExistingKey;

            byte[]? rented = null;
            Span<byte> asciiStringBuf = stackSpan;

            string provName = GetStringProvParam(handle, CryptProvParam.PP_NAME, ref asciiStringBuf, ref rented, 0);
            int maxClear = provName.Length;
            string keyName = GetStringProvParam(handle, CryptProvParam.PP_CONTAINER, ref asciiStringBuf, ref rented, maxClear);
            maxClear = Math.Max(maxClear, keyName.Length);

            if (rented != null)
            {
                CryptoPool.Return(rented, maxClear);
            }

            Debug.Assert(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
            return new CspParameters(provType)
            {
                Flags = provFlags,
                KeyContainerName = keyName,
                ProviderName = provName,
            };
        }

        private static string GetStringProvParam(
            SafeProvOrNCryptKeyHandle handle,
            CryptProvParam dwParam,
            ref Span<byte> buf,
            ref byte[]? rented,
            int clearLen)
        {
            int len = buf.Length;

            if (!Interop.Advapi32.CryptGetProvParam(handle, dwParam, buf, ref len))
            {
                if (len > buf.Length)
                {
                    if (rented != null)
                    {
                        CryptoPool.Return(rented, clearLen);
                    }

                    rented = CryptoPool.Rent(len);
                    buf = rented;
                    len = rented.Length;
                }
                else
                {
                    throw Marshal.GetLastPInvokeError().ToCryptographicException();
                }

                if (!Interop.Advapi32.CryptGetProvParam(handle, dwParam, buf, ref len))
                {
                    throw Marshal.GetLastPInvokeError().ToCryptographicException();
                }
            }

            unsafe
            {
                fixed (byte* asciiPtr = &MemoryMarshal.GetReference(buf))
                {
                    return Marshal.PtrToStringAnsi((IntPtr)asciiPtr, len);
                }
            }
        }

        private static unsafe CryptographicAttributeObjectCollection ToCryptographicAttributeObjectCollection(CRYPT_ATTRIBUTES* pCryptAttributes)
        {
            CryptographicAttributeObjectCollection collection = new CryptographicAttributeObjectCollection();
            for (int i = 0; i < pCryptAttributes->cAttr; i++)
            {
                CRYPT_ATTRIBUTE* pCryptAttribute = &(pCryptAttributes->rgAttr[i]);
                AddCryptAttribute(collection, pCryptAttribute);
            }
            return collection;
        }

        private static unsafe void AddCryptAttribute(CryptographicAttributeObjectCollection collection, CRYPT_ATTRIBUTE* pCryptAttribute)
        {
            string oidValue = pCryptAttribute->pszObjId.ToStringAnsi();
            Oid oid = new Oid(oidValue);
            AsnEncodedDataCollection attributeCollection = new AsnEncodedDataCollection();

            for (int i = 0; i < pCryptAttribute->cValue; i++)
            {
                // CreateBestPkcs9AttributeObjectAvailable is expected to create a copy of the data so that it has ownership
                // of the underlying data.
                ReadOnlySpan<byte> encodedAttribute = pCryptAttribute->rgValue[i].DangerousAsSpan();
                AsnEncodedData attributeObject = PkcsHelpers.CreateBestPkcs9AttributeObjectAvailable(oid, encodedAttribute);
                attributeCollection.Add(attributeObject);
            }

            collection.Add(new CryptographicAttributeObject(oid, attributeCollection));
        }
    }
}
