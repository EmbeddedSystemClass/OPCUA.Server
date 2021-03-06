//<summary>
//  Title   : UAServer.UserAuthentication
//  System  : Microsoft Visual C# .NET 2008
//  $LastChangedDate:$
//  $Rev:$
//  $LastChangedBy:$
//  $URL:$
//  $Id:$
//
//  Copyright (C)2009, CAS LODZ POLAND.
//  TEL: +48 (42) 686 25 47
//  mailto://techsupp@cas.eu
//  http://www.cas.eu
//</summary>

using System;
using System.Collections.Generic;
using System.IdentityModel.Selectors;
using System.IdentityModel.Tokens;
using System.Security.Cryptography.X509Certificates;
using System.ServiceModel.Security;
using System.Text;
using System.Xml;
using Opc.Ua;
using Opc.Ua.Server;

namespace CAS.UA.Server.Library
{
  public partial class UAServer
  {
    #region User Validation Functions
    /// <summary>
    /// Creates the objects used to validate the user identity tokens supported by the server.
    /// </summary>
    private void CreateUserIdentityValidators( ApplicationConfiguration configuration )
    {
      for ( int ii = 0; ii < configuration.ServerConfiguration.UserTokenPolicies.Count; ii++ )
      {
        UserTokenPolicy policy = configuration.ServerConfiguration.UserTokenPolicies[ ii ];

        // ignore policies without an explicit id.
        if ( String.IsNullOrEmpty( policy.PolicyId ) )
        {
          continue;
        }

        // create a validator for an issued token policy.
        if ( policy.TokenType == UserTokenType.IssuedToken )
        {
          // the name of the element in the configuration file.
          XmlQualifiedName qname = new XmlQualifiedName( policy.PolicyId, Namespaces.OpcUa );

          // find the id for the issuer certificate.
          CertificateIdentifier id = configuration.ParseExtension<CertificateIdentifier>( qname );

          if ( id == null )
          {
            Utils.Trace(
                (int)Utils.TraceMasks.Error,
                "Could not load CertificateIdentifier for UserTokenPolicy {0}",
                policy.PolicyId );

            continue;
          }

          m_tokenResolver = CreateSecurityTokenResolver( id );
          m_tokenSerializer = WSSecurityTokenSerializer.DefaultInstance;
        }

        // create a validator for a certificate token policy.
        if ( policy.TokenType == UserTokenType.Certificate )
        {
          // the name of the element in the configuration file.
          XmlQualifiedName qname = new XmlQualifiedName( policy.PolicyId, Namespaces.OpcUa );

          // find the location of the trusted issuers.
          CertificateTrustList trustedIssuers = configuration.ParseExtension<CertificateTrustList>( qname );

          if ( trustedIssuers == null )
          {
            Utils.Trace(
                (int)Utils.TraceMasks.Error,
                "Could not load CertificateTrustList for UserTokenPolicy {0}",
                policy.PolicyId );

            continue;
          }

          // trusts any certificate in the trusted people store.
          m_certificateValidator = X509CertificateValidator.PeerTrust;
        }
      }
    }

    /// <summary>
    /// Called when a client tries to change its user identity.
    /// </summary>
    private void SessionManager_ImpersonateUser( Session session, ImpersonateEventArgs args )
    {
      // check for a WSS token.
      IssuedIdentityToken wssToken = args.NewIdentity as IssuedIdentityToken;

      if ( wssToken != null )
      {
        SecurityToken samlToken = ParseAndVerifySamlToken( wssToken.DecryptedTokenData );
        args.Identity = new UserIdentity( samlToken );
        Utils.Trace( "SAML Token Accepted: {0}", args.Identity.DisplayName );
        return;
      }

      // check for a user name token.
      UserNameIdentityToken userNameToken = args.NewIdentity as UserNameIdentityToken;

      if ( userNameToken != null )
      {
        VerifyPassword( userNameToken.UserName, userNameToken.DecryptedPassword );
        args.Identity = new UserIdentity( userNameToken );
        Utils.Trace( "UserName Token Accepted: {0}", args.Identity.DisplayName );
        return;
      }

      // check for x509 user token.
      X509IdentityToken x509Token = args.NewIdentity as X509IdentityToken;

      if ( userNameToken != null )
      {
        VerifyCertificate( x509Token.Certificate );
        args.Identity = new UserIdentity( x509Token );
        Utils.Trace( "X509 Token Accepted: {0}", args.Identity.DisplayName );
        return;
      }
    }

    /// <summary>
    /// Initializes the validator from the configuration for a token policy.
    /// </summary>
    /// <param name="issuerCertificate">The issuer certificate.</param>
    private SecurityTokenResolver CreateSecurityTokenResolver( CertificateIdentifier issuerCertificate )
    {
      if ( issuerCertificate == null )
      {
        throw new ArgumentNullException( "issuerCertificate" );
      }

      // find the certificate.
      X509Certificate2 certificate = issuerCertificate.Find( false );

      if ( certificate == null )
      {
        throw ServiceResultException.Create(
            StatusCodes.BadCertificateInvalid,
            "Could not find issuer certificate: {0}",
            issuerCertificate );
      }

      // create a security token representing the certificate.
      List<SecurityToken> tokens = new List<SecurityToken>();
      tokens.Add( new X509SecurityToken( certificate ) );

      // create issued token resolver.
      SecurityTokenResolver tokenResolver = SecurityTokenResolver.CreateDefaultSecurityTokenResolver(
          new System.Collections.ObjectModel.ReadOnlyCollection<SecurityToken>( tokens ),
          false );

      return tokenResolver;
    }

    /// <summary>
    /// Validates the password for a username token.
    /// </summary>
    private void VerifyPassword( string userName, string password )
    {
      if ( String.IsNullOrEmpty( password ) )
      {
        // construct translation object with default text.
        TranslationInfo info = new TranslationInfo(
            "InvalidPassword",
            "en-US",
            "Specified password is not valid for user '{0}'.",
            userName );

        // create an exception with a vendor defined sub-code.
        throw new ServiceResultException( new ServiceResult(
            StatusCodes.BadIdentityTokenRejected,
            "InvalidPassword",
            "http://opcfoundation.org/UA/Sample/",
            new LocalizedText( info ) ) );
      }
    }

    /// <summary>
    /// Verifies that a certificate user token is trusted.
    /// </summary>
    private void VerifyCertificate( X509Certificate2 certificate )
    {
      try
      {
        m_certificateValidator.Validate( certificate );
      }
      catch ( Exception e )
      {
        // construct translation object with default text.
        TranslationInfo info = new TranslationInfo(
            "InvalidCertificate",
            "en-US",
            "'{0}' is not a trusted user certificate.",
            certificate.Subject );

        // create an exception with a vendor defined sub-code.
        throw new ServiceResultException( new ServiceResult(
            e,
            StatusCodes.BadIdentityTokenRejected,
            "InvalidCertificate",
            "http://opcfoundation.org/UA/Sample/",
            new LocalizedText( info ) ) );
      }
    }

    /// <summary>
    /// Validates a SAML WSS user token.
    /// </summary>
    private SecurityToken ParseAndVerifySamlToken( byte[] tokenData )
    {
      XmlDocument document = new XmlDocument();
      XmlNodeReader reader = null;

      try
      {
        string text = new UTF8Encoding().GetString( tokenData );
        document.InnerXml = text.Trim();

        if ( document.DocumentElement.NamespaceURI != "urn:oasis:names:tc:SAML:1.0:assertion" )
        {
          throw new ServiceResultException( StatusCodes.BadNotSupported );
        }

        reader = new XmlNodeReader( document.DocumentElement );

        SecurityToken samlToken = new SamlSerializer().ReadToken(
            reader,
            m_tokenSerializer,
            m_tokenResolver );

        return samlToken;
      }
      catch ( Exception e )
      {
        // construct translation object with default text.
        TranslationInfo info = new TranslationInfo(
            "InvalidSamlToken",
            "en-US",
            "'{0}' is not a valid SAML token.",
            document.DocumentElement.LocalName );

        // create an exception with a vendor defined sub-code.
        throw new ServiceResultException( new ServiceResult(
            e,
            StatusCodes.BadIdentityTokenRejected,
            "InvalidSamlToken",
            "http://opcfoundation.org/UA/Sample/",
            new LocalizedText( info ) ) );
      }
      finally
      {
        if ( reader != null )
        {
          reader.Close();
        }
      }
    }
    #endregion

    #region Private Fields
    private SecurityTokenResolver m_tokenResolver;
    private SecurityTokenSerializer m_tokenSerializer;
    private X509CertificateValidator m_certificateValidator;
    #endregion
  }
}
