﻿using System;
using System.Threading.Tasks;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage;

namespace FileManager.Class
{
    /// <summary>
    /// Windows Hello授权管理器
    /// </summary>
    public static class WindowsHelloAuthenticator
    {
        /// <summary>
        /// 质询内容
        /// </summary>
        private const string ChallengeText = "This is a challenge send by RX, to verify secure area access authorization";

        /// <summary>
        /// 凭据保存的名称
        /// </summary>
        private const string CredentialName = "RX-SecureProtection";

        /// <summary>
        /// 检查系统是否支持Windows Hello
        /// </summary>
        /// <returns></returns>
        public static Task<bool> CheckSupportAsync()
        {
            return KeyCredentialManager.IsSupportedAsync().AsTask();
        }

        /// <summary>
        /// 请求注册用户
        /// </summary>
        /// <returns></returns>
        public static async Task<AuthenticatorState> RegisterUserAsync()
        {
            if (await CheckSupportAsync().ConfigureAwait(false))
            {
                KeyCredentialRetrievalResult CredentiaResult = await KeyCredentialManager.RequestCreateAsync(CredentialName, KeyCredentialCreationOption.ReplaceExisting);
                switch (CredentiaResult.Status)
                {
                    case KeyCredentialStatus.Success:
                        {
                            string PublicKey = CryptographicBuffer.EncodeToHexString(CredentiaResult.Credential.RetrievePublicKey());
                            ApplicationData.Current.LocalSettings.Values["WindowsHelloPublicKeyForUser"] = PublicKey;
                            return AuthenticatorState.RegisterSuccess;
                        }
                    case KeyCredentialStatus.UserCanceled:
                        {
                            return AuthenticatorState.UserCanceled;
                        }
                    default:
                        {
                            return AuthenticatorState.UnknownError;
                        }
                }
            }
            else
            {
                return AuthenticatorState.WindowsHelloUnsupport;
            }
        }

        /// <summary>
        /// 认证用户授权
        /// </summary>
        /// <returns></returns>
        public static async Task<AuthenticatorState> VerifyUserAsync()
        {
            if (await CheckSupportAsync().ConfigureAwait(false))
            {
                if (ApplicationData.Current.LocalSettings.Values["WindowsHelloPublicKeyForUser"] is string PublicKey)
                {
                    KeyCredentialRetrievalResult RetrievalResult = await KeyCredentialManager.OpenAsync(CredentialName);
                    switch (RetrievalResult.Status)
                    {
                        case KeyCredentialStatus.Success:
                            {
                                KeyCredentialOperationResult OperationResult = await RetrievalResult.Credential.RequestSignAsync(CryptographicBuffer.ConvertStringToBinary(ChallengeText, BinaryStringEncoding.Utf8));
                                if (OperationResult.Status == KeyCredentialStatus.Success)
                                {
                                    var Algorithm = AsymmetricKeyAlgorithmProvider.OpenAlgorithm(AsymmetricAlgorithmNames.RsaSignPkcs1Sha256);
                                    var Key = Algorithm.ImportPublicKey(CryptographicBuffer.DecodeFromHexString(PublicKey));
                                    return CryptographicEngine.VerifySignature(Key, CryptographicBuffer.ConvertStringToBinary(ChallengeText, BinaryStringEncoding.Utf8), OperationResult.Result) ? AuthenticatorState.VerifyPassed : AuthenticatorState.VerifyFailed;
                                }
                                else
                                {
                                    return AuthenticatorState.UnknownError;
                                }
                            }
                        case KeyCredentialStatus.NotFound:
                            {
                                return AuthenticatorState.CredentialNotFound;
                            }
                        default:
                            {
                                return AuthenticatorState.UnknownError;
                            }
                    }
                }
                else
                {
                    return AuthenticatorState.UserNotRegistered;
                }
            }
            else
            {
                return AuthenticatorState.WindowsHelloUnsupport;
            }
        }

        /// <summary>
        /// 删除并注销用户
        /// </summary>
        /// <returns></returns>
        public static async Task DeleteUserAsync()
        {
            if (await CheckSupportAsync().ConfigureAwait(false))
            {
                try
                {
                    ApplicationData.Current.LocalSettings.Values["WindowsHelloPublicKeyForUser"] = null;
                    await KeyCredentialManager.DeleteAsync(CredentialName);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
