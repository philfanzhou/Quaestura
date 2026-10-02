import { type AxiosInstance } from 'axios'
import httpClient from './httpClient'
import type { Tag, CreateTagRequest, PagedResult, SingleResult, MutationResult } from '../types'

class TagAdminApiClient {
  private client: AxiosInstance
  constructor() {
    // Shared Cookie/CSRF client and 401 handling (see httpClient.ts).
    this.client = httpClient
  }
  async list(params: { name?: string; sortBy?: string; page?: number; size?: number }): Promise<PagedResult<Tag>> {
    const response = await this.client.get<PagedResult<Tag>>('/admin/tags', { params })
    return response.data
  }
  async getById(id: string): Promise<Tag> {
    const response = await this.client.get<SingleResult<Tag>>(`/admin/tags/${id}`)
    return response.data.data
  }
  async upsert(request: CreateTagRequest): Promise<MutationResult> {
    const response = await this.client.post<MutationResult>('/admin/tags', request)
    return response.data
  }
  async delete(id: string): Promise<MutationResult> {
    const response = await this.client.delete<MutationResult>(`/admin/tags/${id}`)
    return response.data
  }
}

let _instance: TagAdminApiClient | null = null
export function getTagAdminApiClient(): TagAdminApiClient {
  if (!_instance) _instance = new TagAdminApiClient()
  return _instance
}
