﻿//_______________________________________________________________
//  Title   : ApplicationCertificate
//  System  : Microsoft VisualStudio 2015 / C#
//  $LastChangedDate:  $
//  $Rev: $
//  $LastChangedBy: $
//  $URL: $
//  $Id:  $
//
//  Copyright (C) 2016, CAS LODZ POLAND.
//  TEL: +48 (42) 686 25 47
//  mailto://techsupp@cas.eu
//  http://www.cas.eu
//_______________________________________________________________

using Opc.Ua;
using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace CAS.CommServer.UA.Server.Service
{
  /// <summary>
  /// Class ApplicationCertificate - helper class providing application instance certificate management functionality
  /// </summary>
  /// <remarks>
  /// This class contains selected methods copied from Opc.Ua.Client.Controls.GuiUtils
  /// </remarks>
  internal static class ApplicationCertificate
  {
    /// <summary>
    /// Creates an application instance certificate if one does not already exist.
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    /// <param name="keySize">Size of the key.</param>
    /// <param name="dialogResult">The dialog result returning user decision obtained from MessageBox. Parameters: message, caption.</param>
    /// <param name="updateFile">if set to <c>true</c> [update file].</param>
    /// <returns>X509Certificate2.</returns>
    public static X509Certificate2 CheckApplicationInstanceCertificate(
        ApplicationConfiguration configuration,
        ushort keySize,
        Func<string, string, bool> dialogResult,
        bool updateFile)
    {
      // create a default certificate id none specified.
      CertificateIdentifier id = configuration.SecurityConfiguration.ApplicationCertificate;

      if (id == null)
      {
        id = new CertificateIdentifier();
        id.StoreType = Utils.DefaultStoreType;
        id.StorePath = Utils.DefaultStorePath;
        id.SubjectName = configuration.ApplicationName;
      }

      bool createNewCertificate = false;
      IList<string> serverDomainNames = configuration.GetServerDomainNames();

      // check for private key.
      X509Certificate2 certificate = id.Find(true);

      if (certificate == null)
      {
        // check if config file has wrong thumprint.
        if (!String.IsNullOrEmpty(id.SubjectName) && !String.IsNullOrEmpty(id.Thumbprint))
        {
          CertificateIdentifier id2 = new CertificateIdentifier();
          id2.StoreType = id.StoreType;
          id2.StorePath = id.StorePath;
          id2.SubjectName = id.SubjectName;
          id = id2;

          certificate = id2.Find(true);

          if (certificate != null)
          {
            string message = Utils.Format(
                "Matching certificate with SubjectName={0} found but with a different thumbprint. Use certificate?",
                id.SubjectName);

            if (dialogResult(message, configuration.ApplicationName))
            {
              certificate = null;
            }
          }
        }
      }

      // check if private key is missing.
      if (certificate == null)
      {
        certificate = id.Find(false);

        if (certificate != null)
        {
          string message = Utils.Format(
              "Matching certificate with SubjectName={0} found but without a private key. Create a new certificate?",
              id.SubjectName);

          if (dialogResult(message, configuration.ApplicationName))
          {
            certificate = null;
          }
        }
      }

      // check domains.
      if (certificate != null)
      {
        IList<string> certificateDomainNames = Utils.GetDomainsFromCertficate(certificate);

        for (int ii = 0; ii < serverDomainNames.Count; ii++)
        {
          if (Utils.FindStringIgnoreCase(certificateDomainNames, serverDomainNames[ii]))
          {
            continue;
          }

          if (String.Compare(serverDomainNames[ii], "localhost", StringComparison.OrdinalIgnoreCase) == 0)
          {
            // check computer name.
            string computerName = System.Net.Dns.GetHostName();

            if (Utils.FindStringIgnoreCase(certificateDomainNames, computerName))
            {
              continue;
            }

            // check for aliases.
            System.Net.IPHostEntry entry = System.Net.Dns.GetHostEntry(computerName);

            bool found = false;

            for (int jj = 0; jj < entry.Aliases.Length; jj++)
            {
              if (Utils.FindStringIgnoreCase(certificateDomainNames, entry.Aliases[jj]))
              {
                found = true;
                break;
              }
            }

            if (found)
            {
              continue;
            }

            // check for ip addresses.
            for (int jj = 0; jj < entry.AddressList.Length; jj++)
            {
              if (Utils.FindStringIgnoreCase(certificateDomainNames, entry.AddressList[jj].ToString()))
              {
                found = true;
                break;
              }
            }

            if (found)
            {
              continue;
            }
          }

          string message = Utils.Format(
              "The server is configured to use domain '{0}' which does not appear in the certificate. Update certificate?",
              serverDomainNames[ii]);

          createNewCertificate = true;

          if (!dialogResult(message, configuration.ApplicationName))
          {
            createNewCertificate = false;
            continue;
          }

          Utils.Trace(message);
          break;
        }

        if (!createNewCertificate)
        {
          // check if key size matches.
          if (keySize == certificate.PublicKey.Key.KeySize)
          {
            AddToTrustedPeerStore(configuration, certificate);
            return certificate;
          }
        }
      }

      // prompt user.
      if (!createNewCertificate)
      {
        if (!dialogResult("Application does not have an instance certificate. Create one automatically?", configuration.ApplicationName))
        {
          return null;
        }
      }

      // delete existing certificate.
      if (certificate != null)
      {
        DeleteApplicationInstanceCertificate(configuration);
      }

      // add the localhost.
      if (serverDomainNames.Count == 0)
      {
        serverDomainNames.Add(System.Net.Dns.GetHostName());
      }

      certificate = Opc.Ua.CertificateFactory.CreateCertificate(
          id.StoreType,
          id.StorePath,
          configuration.ApplicationUri,
          configuration.ApplicationName,
          null,
          serverDomainNames,
          keySize,
          300);

      id.Certificate = certificate;
      AddToTrustedPeerStore(configuration, certificate);

      if (updateFile && !String.IsNullOrEmpty(configuration.SourceFilePath))
      {
        configuration.SaveToFile(configuration.SourceFilePath);
      }

      configuration.CertificateValidator.Update(configuration.SecurityConfiguration);

      return configuration.SecurityConfiguration.ApplicationCertificate.LoadPrivateKey(null);
    }
    /// <summary>
    /// Uses the command line to override the UA TCP implementation specified in the configuration.
    /// </summary>
    /// <param name="configuration">The configuration instance that stores the configurable information for a UA application.
    /// </param>
    public static void OverrideUaTcpImplementation(ApplicationConfiguration configuration)
    {
      // check if UA TCP configuration included.
      TransportConfiguration transport = null;

      for (int ii = 0; ii < configuration.TransportConfigurations.Count; ii++)
      {
        if (configuration.TransportConfigurations[ii].UriScheme == Utils.UriSchemeOpcTcp)
        {
          transport = configuration.TransportConfigurations[ii];
          break;
        }
      }

      // check if UA TCP implementation explicitly specified.
      if (transport != null)
      {
        string[] args = Environment.GetCommandLineArgs();

        if (args != null && args.Length > 1)
        {
          if (String.Compare(args[1], "-uaTcpAnsiC", StringComparison.InvariantCultureIgnoreCase) == 0)
          {
            transport.TypeName = Utils.UaTcpBindingNativeStack;
          }
          else if (String.Compare(args[1], "-uaTcpDotNet", StringComparison.InvariantCultureIgnoreCase) == 0)
          {
            transport.TypeName = Utils.UaTcpBindingDefault;
          }
        }
      }
    }

    /// <summary>
    /// The extension method handles a certificate validation error and produces message to be displayed.
    /// </summary>
    public static string HandleCertificateValidationError(this CertificateValidationEventArgs certificateValidationEventArgs)
    {
      if (certificateValidationEventArgs == null)
        throw new ArgumentNullException(nameof(certificateValidationEventArgs));
      StringBuilder buffer = new StringBuilder();
      buffer.AppendFormat("Certificate could not be validated: {0}\r\n\r\n", certificateValidationEventArgs.Error.StatusCode);
      buffer.AppendFormat("Subject: {0}\r\n", certificateValidationEventArgs.Certificate.Subject);
      buffer.AppendFormat("Issuer: {0}\r\n", (certificateValidationEventArgs.Certificate.Subject == certificateValidationEventArgs.Certificate.Issuer) ? "Self-signed" : certificateValidationEventArgs.Certificate.Issuer);
      buffer.AppendFormat("Valid From: {0}\r\n", certificateValidationEventArgs.Certificate.NotBefore);
      buffer.AppendFormat("Valid To: {0}\r\n", certificateValidationEventArgs.Certificate.NotAfter);
      buffer.AppendFormat("Thumbprint: {0}\r\n\r\n", certificateValidationEventArgs.Certificate.Thumbprint);
      buffer.AppendFormat("Accept anyways?");
      return buffer.ToString();
    }
    /// <summary>
    /// Displays the UA-TCP configuration in the form.
    /// </summary>
    /// <param name="assign">The assign.</param>
    /// <param name="formText">The form text.</param>
    /// <param name="configuration">The configuration instance that stores the configurable information for a UA application.</param>
    public static void DisplayUaTcpImplementation(Action<string> assign, string formText, ApplicationConfiguration configuration)
    {
      // check if UA TCP configuration included.
      TransportConfiguration transport = null;

      for (int ii = 0; ii < configuration.TransportConfigurations.Count; ii++)
      {
        if (configuration.TransportConfigurations[ii].UriScheme == Utils.UriSchemeOpcTcp)
        {
          transport = configuration.TransportConfigurations[ii];
          break;
        }
      }

      // check if UA TCP implementation explicitly specified.
      if (transport != null)
      {
        string text = formText;

        int index = text.LastIndexOf("(UA TCP - ");

        if (index >= 0)
        {
          text = text.Substring(0, index);
        }

        if (transport.TypeName == Utils.UaTcpBindingNativeStack)
        {
          assign( Utils.Format("{0} (UA TCP - ANSI C)", text));
        }
        else
        {
          assign(Utils.Format("{0} (UA TCP - C#)", text));
        }
      }
    }

    /// <summary>
    /// Adds the certificate to the Trusted Peer Certificate Store
    /// </summary>
    /// <param name="configuration">The application's configuration which specifies the location of the TrustedPeerStore.</param>
    /// <param name="certificate">The certificate to register.</param>
    private static void AddToTrustedPeerStore(ApplicationConfiguration configuration, X509Certificate2 certificate)
    {
      ICertificateStore store = configuration.SecurityConfiguration.TrustedPeerCertificates.OpenStore();

      try
      {
        // check if it already exists.
        X509Certificate2 certificate2 = store.FindByThumbprint(certificate.Thumbprint);

        if (certificate2 != null)
        {
          return;
        }

        List<string> subjectName = Utils.ParseDistinguishedName(certificate.Subject);

        // check for old certificate.
        X509Certificate2Collection certificates = store.Enumerate();

        for (int ii = 0; ii < certificates.Count; ii++)
        {
          if (Utils.CompareDistinguishedName(certificates[ii], subjectName))
          {
            if (certificates[ii].Thumbprint == certificate.Thumbprint)
            {
              return;
            }

            store.Delete(certificates[ii].Thumbprint);
            break;
          }
        }

        // add new certificate.
        X509Certificate2 publicKey = new X509Certificate2(certificate.GetRawCertData());
        store.Add(publicKey);
      }
      finally
      {
        store.Close();
      }
    }
    /// <summary>
    /// Deletes an existing application instance certificate.
    /// </summary>
    /// <param name="configuration">The configuration instance that stores the configurable information for a UA application.</param>
    private static void DeleteApplicationInstanceCertificate(ApplicationConfiguration configuration)
    {
      // create a default certificate id none specified.
      CertificateIdentifier id = configuration.SecurityConfiguration.ApplicationCertificate;

      if (id == null)
      {
        return;
      }

      // delete private key.
      X509Certificate2 certificate = id.Find();

      // delete trusted peer certificate.
      if (configuration.SecurityConfiguration != null && configuration.SecurityConfiguration.TrustedPeerCertificates != null)
      {
        string thumbprint = id.Thumbprint;

        if (certificate != null)
        {
          thumbprint = certificate.Thumbprint;
        }

        if (!String.IsNullOrEmpty(thumbprint))
        {
          using (ICertificateStore store = configuration.SecurityConfiguration.TrustedPeerCertificates.OpenStore())
          {
            store.Delete(thumbprint);
          }
        }
      }

      // delete private key.
      if (certificate != null)
      {
        using (ICertificateStore store = id.OpenStore())
        {
          store.Delete(certificate.Thumbprint);
        }
      }
    }

  }
}
