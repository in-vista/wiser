﻿using System.Security.Claims;
using System.Threading.Tasks;
using Api.Core.Services;
using Api.Modules.Customers.Enums;
using Api.Modules.Customers.Models;

namespace Api.Modules.Customers.Interfaces
{
    /// <summary>
    /// Interface for operations related to Wiser users (users that can log in to Wiser).
    /// </summary>
    public interface IWiserCustomersService
    {
        /// <summary>
        /// Get a single customer via <see cref="ClaimsIdentity"/>.
        /// </summary>
        /// <param name="identity">The <see cref="ClaimsIdentity">ClaimsIdentity</see> of the authenticated user to check for rights.</param>
        /// <returns>The <see cref="CustomerModel"/>.</returns>
        Task<ServiceResult<CustomerModel>> GetSingleAsync(ClaimsIdentity identity);

        /// <summary>
        /// Get the encryption key for a customer via <see cref="ClaimsIdentity"/>.
        /// </summary>
        /// <param name="identity">The <see cref="ClaimsIdentity">ClaimsIdentity</see> of the authenticated user to check for rights.</param>
        /// <returns>The encryption key as string..</returns>
        Task<ServiceResult<string>> GetEncryptionKey(ClaimsIdentity identity);
        
        /// <summary>
        /// Decrypts a value using the encryption key that is saved for the customer.
        /// This uses the encryption that adds a date, so that these encrypted values expire after a certain amount of time.
        /// </summary>
        /// <typeparam name="T">The return type.</typeparam>
        /// <param name="encryptedValue">The input.</param>
        /// <param name="identity">The identity of the authenticated client. The customerId will be retrieved from this.</param>
        /// <returns>The decrypted value in the requested type.</returns>
        Task<T> DecryptValue<T>(string encryptedValue, ClaimsIdentity identity);

        /// <summary>
        /// Decrypts a value using the encryption key that is saved for the customer.
        /// This uses the encryption that adds a date, so that these encrypted values expire after a certain amount of time.
        /// </summary>
        /// <typeparam name="T">The return type.</typeparam>
        /// <param name="encryptedValue">The input.</param>
        /// <param name="customer">The <see cref="CustomerModel"/>.</param>
        /// <returns>The decrypted value in the requested type.</returns>
        T DecryptValue<T>(string encryptedValue, CustomerModel customer);

        /// <summary>
        /// Encrypts a value using the encryption key that is saved for the customer.
        /// This uses the encryption that adds a date, so that these encrypted values expire after a certain amount of time.
        /// </summary>
        /// <param name="valueToEncrypt">The input.</param>
        /// <param name="identity">The identity of the authenticated client. The customerId will be retrieved from this.</param>
        /// <returns>The decrypted value in the requested type.</returns>
        Task<string> EncryptValue(object valueToEncrypt, ClaimsIdentity identity);

        /// <summary>
        /// Encrypts a value using the encryption key that is saved for the customer.
        /// This uses the encryption that adds a date, so that these encrypted values expire after a certain amount of time.
        /// </summary>
        /// <param name="valueToEncrypt">The input.</param>
        /// <param name="customer">The <see cref="CustomerModel"/>.</param>
        /// <returns>The decrypted value in the requested type.</returns>
        string EncryptValue(object valueToEncrypt, CustomerModel customer);

        /// <summary>
        /// Check if a customer already exists.
        /// </summary>
        /// <param name="name">The name of the customer.</param>
        /// <param name="subDomain">The sub domain of the customer.</param>
        /// <returns>A <see cref="CustomerExistsResults"/>.</returns>
        Task<ServiceResult<CustomerExistsResults>> CustomerExistsAsync(string name, string subDomain);

        /// <summary>
        /// Creates a new Wiser 2 customer.
        /// </summary>
        /// <param name="customer">The data for the new customer.</param>
        /// <param name="isWebShop">Optional: Indicate whether or not this customer is getting a webshop. Default is <see langword="false"/>.</param>
        /// <returns>The newly created <see cref="CustomerModel"/>.</returns>
        Task<ServiceResult<CustomerModel>> CreateCustomerAsync(CustomerModel customer, bool isWebShop = false);

        /// <summary>
        /// Gets the title for the browser tab for a customer, based on sub domain.
        /// </summary>
        /// <param name="subDomain">The sub domain of the customer.</param>
        /// <returns>The title for the browser tab.</returns>
        Task<ServiceResult<string>> GetTitleAsync(string subDomain);
    }
}