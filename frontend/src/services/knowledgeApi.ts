import axios, { type AxiosInstance } from 'axios'
import type { Knowledge, CreateKnowledgeRequest, PagedResult, SingleResult, MutationResult } from '../types'

class KnowledgeAdminApiClient {
  private client: AxiosInstance
  constructor() {
    this.client = axios.create({ timeout: 20000 })
  }
  async list(params: { parentId?: string; grade?: number; subject?: number; name?: string; page?: number; size?: number }): Promise<PagedResult<Knowledge>> {
    const response = await this.client.get<PagedResult<Knowledge>>('/admin/knowledges', { params })
    return response.data
  }
  async getById(id: string): Promise<Knowledge> {
    const response = await this.client.get<SingleResult<Knowledge>>(`/admin/knowledges/${id}`)
    return response.data.data
  }
  async upsert(request: CreateKnowledgeRequest): Promise<MutationResult> {
    const response = await this.client.post<MutationResult>('/admin/knowledges', request)
    return response.data
  }
  async delete(id: string): Promise<MutationResult> {
    const response = await this.client.delete<MutationResult>(`/admin/knowledges/${id}`)
    return response.data
  }
}

let _instance: KnowledgeAdminApiClient | null = null
export function getKnowledgeAdminApiClient(): KnowledgeAdminApiClient {
  if (!_instance) _instance = new KnowledgeAdminApiClient()
  return _instance
}
