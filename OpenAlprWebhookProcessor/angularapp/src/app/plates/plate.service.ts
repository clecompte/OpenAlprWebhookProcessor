import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { Stream } from 'stream';
import { Plate } from './plate/plate';
import { PlateResponse } from './plate/plateResponse';
import { PlateStatistics } from './plate/plateStatistics';
import { VehicleFilters } from './vehicleFilters';
import { GetPlateResponse } from './plate/getPlateResponse';

@Injectable({ providedIn: 'root' })
export class PlateService {
    private getRelayImageUrl = 'images';
    private searchPlatesUrl = 'licenseplates/search';
    private editPlateUrl = 'licenseplates/edit';
    private singlePlateUrl = 'licenseplates';
    private hydrateDatabaseUrl = 'hydration/start';
    private getFiltersUrl = 'licenseplates/filters';
    private getStatistics = 'licenseplates/statistics';
    private enrichPlateUrl = 'licenseplates/enrich';

    constructor(private http: HttpClient) { }

    searchPlates(plateRequest: PlateRequest): Observable<PlateResponse> {
        return this.http.post<PlateResponse>(`/${this.searchPlatesUrl}`, plateRequest);
    }

    upsertPlate(plate: Plate): Observable<void> {
        return this.http.post<void>(`/${this.editPlateUrl}`, plate);
    }

    deletePlate(plateId: string): Observable<void> {
        return this.http.delete<void>(`/${this.singlePlateUrl}/${plateId}`);
    }

    getRelayImage(imageId: string) {
        return this.http.get<Stream>(`/${this.getRelayImageUrl}/${imageId}`);
    }

    hydrateDatabase(): Observable<void> {
        return this.http.post<void>(`/${this.hydrateDatabaseUrl}`, {});
    }

    getFilters(): Observable<VehicleFilters> {
        return this.http.get<VehicleFilters>(`/${this.getFiltersUrl}`);
    }

    getPlate(plateId: string): Observable<GetPlateResponse> {
        return this.http.get<GetPlateResponse>(`/${this.singlePlateUrl}/${plateId}`);
    }

    getPlateStatistics(plateNumber: string): Observable<PlateStatistics> {
        return this.http.get<PlateStatistics>(`/${this.getStatistics}/${plateNumber}`);
    }

    enrichPlate(plateId: string): Observable<void> {
        return this.http.post<void>(`/${this.enrichPlateUrl}/${plateId}`, null);
    }
}

export class PlateRequest {
    plateNumber: string;
    startSearchOn: Date;
    endSearchOn: Date;
    strictMatch: boolean;
    filterIgnoredPlates: boolean;
    filterPlatesSeenLessThan: number;
    regexSearchEnabled: boolean;
    pageNumber: number;
    pageSize: number;
    vehicleMake: string;
    vehicleModel: string;
    vehicleColor: string;
    vehicleType: string;
    vehicleRegion: string;
}
