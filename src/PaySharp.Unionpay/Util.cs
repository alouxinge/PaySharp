﻿using System;
using System.Collections;
using System.IO;
using System.Text;
using ICSharpCode.SharpZipLib.Zip.Compression;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Pkix;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;
using Org.BouncyCastle.X509.Store;
using PaySharp.Core.Utils;
using PaySharp.Unionpay.Properties;

namespace PaySharp.Unionpay
{
    internal static class Util
    {
        #region 字段

        private static Pkcs12Store _pkcs12Store;
        private static string _aliase;

        #endregion

        #region 证书

        private static X509Certificate ACPENCCER = null;
        private static X509Certificate ACPROOTCER = null;
        private static X509Certificate ACPMIDDLECER = null;

        #endregion

        #region 证书操作

        /// <summary>
        /// 获取Pkcs12Store
        /// </summary>
        /// <param name="path">证书路径</param>
        /// <param name="pwd">证书密码</param>
        /// <returns></returns>
        private static void GetPkcs12Store(string path, string pwd)
        {
            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var pkcs12Store = new Pkcs12Store(fileStream, pwd.ToCharArray());
            foreach (string item in pkcs12Store.Aliases)
            {
                if (pkcs12Store.IsKeyEntry(item))
                {
                    _pkcs12Store = pkcs12Store;
                    _aliase = item;
                }
            }
        }

        /// <summary>
        /// 获取证书编号
        /// </summary>
        /// <param name="path">证书路径</param>
        /// <param name="pwd">证书密码</param>
        /// <returns></returns>
        public static string GetCertId(string path, string pwd)
        {
            if (string.IsNullOrEmpty(_aliase))
            {
                GetPkcs12Store(path, pwd);
            }

            return _pkcs12Store
                .GetCertificateChain(_aliase)[0]
                .Certificate
                .SerialNumber
                .ToString();
        }

        /// <summary>
        /// 获取加密证书编号
        /// </summary>
        /// <returns></returns>
        public static string GetEncryptCertId() => GetEncCert().SerialNumber.ToString();

        /// <summary>
        /// 获取证书编号
        /// </summary>
        /// <param name="path">证书路径</param>
        /// <param name="pwd">证书密码</param>
        /// <returns></returns>
        public static AsymmetricKeyParameter GetCertKey(string path, string pwd)
        {
            if (string.IsNullOrEmpty(_aliase))
            {
                GetPkcs12Store(path, pwd);
            }

            return _pkcs12Store
                .GetKey(_aliase)
                .Key;
        }

        #endregion

        #region 签名

        /// <summary>
        /// 签名
        /// </summary>
        /// <param name="key">私钥</param>
        /// <param name="data">数据</param>
        /// <returns></returns>
        public static string Sign(AsymmetricKeyParameter key, string data)
        {
            var byteData = Encoding.UTF8.GetBytes(EncryptUtil.SHA256(data));
            var normalSig = SignerUtilities.GetSigner("SHA256WithRSA");
            normalSig.Init(true, key);
            normalSig.BlockUpdate(byteData, 0, byteData.Length);
            var normalResult = normalSig.GenerateSignature();
            return Convert.ToBase64String(normalResult);
        }

        #endregion

        #region 验签

        /// <summary>
        /// 验签
        /// </summary>
        /// <param name="data">数据</param>
        /// <param name="sign">签名</param>
        /// <param name="signPubKeyCert">验证公钥</param>
        /// <returns></returns>
        public static bool VerifyData(string data, string sign, string signPubKeyCert)
        {
            var dataByte = Encoding.UTF8.GetBytes(EncryptUtil.SHA256(data));
            var signByte = Convert.FromBase64String(sign);

            var x509Cert = VerifyAndGetPubKey(signPubKeyCert);
            return x509Cert == null ? false : VerifySignature(x509Cert.GetPublicKey(), signByte, dataByte);
        }

        private static bool VerifySignature(AsymmetricKeyParameter key, byte[] base64DecodingSignStr, byte[] srcByte)
        {
            var verifier = SignerUtilities.GetSigner("SHA256WithRSA");
            verifier.Init(false, key);
            verifier.BlockUpdate(srcByte, 0, srcByte.Length);
            return verifier.VerifySignature(base64DecodingSignStr);
        }

        private static X509Certificate VerifyAndGetPubKey(string signPubKeyCert)
        {
            var x509Cert = GetPubKeyCert(signPubKeyCert);
            return x509Cert == null ? null : VerifyCertificate(x509Cert) ? x509Cert : null;
        }

        private static bool VerifyCertificate(X509Certificate x509Cert)
        {
            var cn = GetIdentitiesFromCertficate(x509Cert);
            try
            {
                x509Cert.CheckValidity();//验证有效期
                if (!VerifyCertificateChain(x509Cert))//验证书链
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            var unionpayCnName = "中国银联股份有限公司";
            return unionpayCnName.Equals(cn) || "00040000:SIGN".Equals(cn);
        }

        private static bool VerifyCertificateChain(X509Certificate cert)
        {
            if (cert is null)
            {
                return false;
            }

            var rootCert = GetRootCert();
            if (rootCert is null)
            {
                return false;
            }

            var middleCert = GetMiddleCert();
            if (middleCert is null)
            {
                return false;
            }

            try
            {
                var selector = new X509CertStoreSelector
                {
                    Subject = cert.SubjectDN
                };

                ISet trustAnchors = new HashSet
                {
                    new TrustAnchor(rootCert, null)
                };
                var pkixParams = new PkixBuilderParameters(trustAnchors, selector);

                IList intermediateCerts = new ArrayList
                {
                    rootCert,
                    middleCert,
                    cert
                };

                pkixParams.IsRevocationEnabled = false;

                var intermediateCertStore = X509StoreFactory.Create(
                    "Certificate/Collection",
                    new X509CollectionStoreParameters(intermediateCerts));
                pkixParams.AddStore(intermediateCertStore);

                var pathBuilder = new PkixCertPathBuilder();
                var result = pathBuilder.Build(pkixParams);
                var path = result.CertPath;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static X509Certificate GetCert(byte[] input)
        {
            try
            {
                return new X509CertificateParser().ReadCertificate(input);
            }
            catch
            {
                return null;
            }
        }

        private static X509Certificate GetRootCert()
        {
            try
            {
                if (ACPROOTCER is null)
                {
#if DEBUG
                    ACPROOTCER = GetCert(Resources.acp_test_root);
#else
                    ACPROOTCER= GetCert(Resources.acp_prod_root);
#endif
                }

                return ACPROOTCER;
            }
            catch
            {
                return null;
            }
        }

        private static X509Certificate GetMiddleCert()
        {
            try
            {
                if (ACPMIDDLECER is null)
                {
#if DEBUG
                    ACPMIDDLECER = GetCert(Resources.acp_test_middle);
#else
                    ACPMIDDLECER= GetCert(Resources.acp_prod_middle);
#endif
                }

                return ACPMIDDLECER;
            }
            catch
            {
                return null;
            }
        }

        private static X509Certificate GetEncCert()
        {
            try
            {
                if (ACPENCCER is null)
                {
#if DEBUG
                    ACPENCCER = GetCert(Resources.acp_test_enc);
#else
                    ACPENCCER= GetCert(Resources.acp_prod_enc);
#endif
                }

                return ACPENCCER;
            }
            catch
            {
                return null;
            }
        }

        private static string GetIdentitiesFromCertficate(X509Certificate aCert)
        {
            var tDN = aCert.SubjectDN.ToString();
            var tPart = "";
            if (tDN != null)
            {
                var tSplitStr = tDN.Substring(tDN.IndexOf("CN=")).Split("@".ToCharArray());
                if (tSplitStr != null && tSplitStr.Length > 2 && tSplitStr[2] != null)
                {
                    tPart = tSplitStr[2];
                }
            }
            return tPart;
        }

        private static X509Certificate GetPubKeyCert(string pubKeyCert)
        {
            try
            {
                pubKeyCert = pubKeyCert
                    .Replace("-----END CERTIFICATE-----", "")
                    .Replace("-----BEGIN CERTIFICATE-----", "");
                var x509CertBytes = Convert.FromBase64String(pubKeyCert);
                var cf = new X509CertificateParser();
                var x509Cert = cf.ReadCertificate(x509CertBytes);
                return x509Cert;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region 加密

        /// <summary>
        /// 加密
        /// </summary>
        /// <param name="data">数据</param>
        /// <returns></returns>
        public static string Encrypt(string data)
        {
            var c = CipherUtilities.GetCipher("RSA/NONE/PKCS1Padding");
            c.Init(true, new ParametersWithRandom(GetEncCert().GetPublicKey(), new SecureRandom()));
            return Convert.ToBase64String(c.DoFinal(Encoding.UTF8.GetBytes(data)));
        }

        #endregion

        #region 解密

        /// <summary>
        /// 解密
        /// </summary>
        /// <param name="key">私钥</param>
        /// <param name="data">数据</param>
        /// <returns></returns>
        public static string Decrypt(AsymmetricKeyParameter key, string data)
        {
            var dataByte = Convert.FromBase64String(data);
            var c = CipherUtilities.GetCipher("RSA/NONE/PKCS1Padding");
            c.Init(false, key);
            return Encoding.UTF8.GetString(c.DoFinal(dataByte));
        }

        #endregion

        #region Inflater解压缩

        /// <summary>
        /// Inflater解压缩
        /// </summary>
        /// <param name="data">数据</param>
        /// <returns></returns>
        public static byte[] Inflater(string data)
        {
            var temp = new byte[1024];
            var memory = new MemoryStream();
            var inf = new Inflater();
            inf.SetInput(Convert.FromBase64String(data));
            while (!inf.IsFinished)
            {
                var extracted = inf.Inflate(temp);
                if (extracted > 0)
                {
                    memory.Write(temp, 0, extracted);
                }
                else
                {
                    break;
                }
            }
            return memory.ToArray();
        }

        #endregion
    }
}
