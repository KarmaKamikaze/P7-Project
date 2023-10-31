﻿using ChatRPG.Data.Models;

namespace ChatRPG.Services;

/// <summary>
/// Service for persisting and loading changes from the data model.
/// </summary>
public interface IPersisterService
{
    /// <summary>
    /// Saves the given <paramref name="campaign"/> and all its related entities.
    /// </summary>
    /// <param name="campaign">The campaign to save.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SaveAsync(Campaign campaign);
    /// <summary>
    /// Loads the campaign with the given <paramref name="campaignId"/> with all its related entities.
    /// </summary>
    /// <param name="campaignId">Id of the campaign to load.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task<Campaign> LoadFromCampaignIdAsync(int campaignId);
}